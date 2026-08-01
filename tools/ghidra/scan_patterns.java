// scan_patterns.java — mass-scan a TSV of AOB signatures against every executable block and
// resolve each hit exactly like Genau::TryResolveMatch (RipDirect / RipBoth, with adjustment),
// classifying it against the known-true VA for that target.
//
// Supports FULL-byte `??` AND NIBBLE wildcards (`4?` / `?5`) — same semantics as
// Macht::ParsePattern (per-byte AND mask 0x00/0x0F/0xF0/0xFF, bytes pre-masked).
//
// Single pass over .text with an anchor-byte bucket map, so all ~150 patterns cost one sweep.
//
// env GS_TSV   = path to patterns.tsv (id target resolve io opc tot adj pri src pattern note)
// env GS_TRUE  = comma-separated "Target=va[|va2...]" entries.
//                An entry may be PREFIXED with a program-name substring and a colon:
//                    "-CoreUObject-:GObjects=1803f9210,-Engine-:GWorld=18182a0b8"
//                Unprefixed entries apply to every program. This matters because the DLLs of a
//                modular build share image base 0x180000000, so their address ranges OVERLAP —
//                a plain union of truths would let a hit in Core be scored "correct GObjects"
//                when GObjects does not live in Core at all. Use distinguishing substrings
//                (`-Core-Win64` vs `-CoreUObject-Win64`).
// env GS_OUT   = output dir
// env GS_TAG   = optional label for the project (defaults to the program name)
//
// OUTPUTS — one set per PROGRAM. The previous version wrote a fixed "scan_patterns.txt", so a
// `-process` run over a modular project silently overwrote itself and only the last DLL
// survived; that is why modular projects had to be scanned one `-process <dll>` at a time.
//   scan_<prog>.txt        human-readable, per-target, with the >>> SELECTED regression line
//   scan_<prog>.tsv        one row per pattern — hotspot/cost table for cross-binary aggregation
//   consensus_<prog>.txt   per-target address consensus: which VA the most INDEPENDENT patterns
//                          agree on. On a symbol-less binary this is the only correctness signal
//                          there is, and it was validated against Everspace (the pre-PDB
//                          consensus matched the PDB symbols exactly once the PDB arrived).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.*;
import java.io.*;
import java.util.*;

public class scan_patterns extends GhidraScript {

    static class Sig {
        String id, target, resolve, src, note, pat;
        int io, opc, tot, adj, pri;
        byte[] bytes, mask;
        int anchor = -1;
        int patLen = 0;      // pattern length in bytes
        int litBytes = 0;    // FULLY literal bytes (both nibbles fixed) — the specificity metric
        int litNibbles = 0;  // fixed nibbles (a nibble-wildcarded byte contributes 1, not 2)
        long nHits = 0;      // TOTAL hits, uncapped (the `hits` list below is capped for memory,
                             // and the old code reported hits.size(), under-counting hot patterns)
        List<long[]> hits = new ArrayList<>();   // {matchVA, directTarget, derefTarget}
    }

    static final int HIT_DETAIL_CAP = 40000;

    static boolean parse(Sig s) {
        String[] t = s.pat.trim().split("\\s+");
        s.bytes = new byte[t.length];
        s.mask = new byte[t.length];
        s.patLen = t.length;
        for (int i = 0; i < t.length; i++) {
            String tok = t[i];
            if (tok.length() != 2) return false;
            char hi = tok.charAt(0), lo = tok.charAt(1);
            int m = 0, v = 0;
            if (hi == '?') { m |= 0x00; } else { m |= 0xF0; v |= Character.digit(hi, 16) << 4; }
            if (lo == '?') { m |= 0x00; } else { m |= 0x0F; v |= Character.digit(lo, 16); }
            if (hi != '?' && Character.digit(hi, 16) < 0) return false;
            if (lo != '?' && Character.digit(lo, 16) < 0) return false;
            s.mask[i] = (byte) m;
            s.bytes[i] = (byte) (v & m);
            if ((m & 0xFF) == 0xFF) s.litBytes++;
            if (hi != '?') s.litNibbles++;
            if (lo != '?') s.litNibbles++;
        }
        for (int i = 0; i < s.mask.length; i++)
            if ((s.mask[i] & 0xFF) == 0xFF) { s.anchor = i; break; }
        return s.anchor >= 0;
    }

    static String sanitize(String n) { return n.replaceAll("[^A-Za-z0-9._-]", "_"); }

    // A resolved global must live in a NON-EXECUTABLE block (.data / .rdata / .bss). Used to keep
    // the consensus table clean: a "target" that lands inside .text is a mis-resolution, not a
    // candidate global.
    private boolean isDataAddr(Memory mem, long va) {
        if (va < 0x10000L) return false;
        try {
            MemoryBlock b = mem.getBlock(toAddr(va));
            return b != null && !b.isExecute();
        } catch (Throwable t) { return false; }
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        String tsv = System.getenv("GS_TSV");
        String trueSpec = System.getenv("GS_TRUE");
        String tag = System.getenv("GS_TAG");
        String prog = currentProgram.getName();
        if (tag == null || tag.isEmpty()) tag = prog;
        String progLower = prog.toLowerCase();

        Map<String, List<Long>> truth = new LinkedHashMap<>();
        if (trueSpec != null && !trueSpec.trim().isEmpty()) for (String kv0 : trueSpec.split(",")) {
            String kv = kv0.trim();
            if (kv.isEmpty()) continue;
            int colon = kv.indexOf(':');
            if (colon > 0) {                       // program-scoped entry
                String want = kv.substring(0, colon).trim().toLowerCase();
                if (!progLower.contains(want)) continue;
                kv = kv.substring(colon + 1).trim();
            }
            String[] p = kv.split("=");
            if (p.length < 2) continue;
            List<Long> vs = new ArrayList<>();
            for (String v : p[1].trim().split("\\|")) vs.add(Long.parseLong(v.trim(), 16));
            truth.put(p[0].trim(), vs);
        }

        List<Sig> sigs = new ArrayList<>();
        BufferedReader br = new BufferedReader(new FileReader(tsv));
        String line = br.readLine();   // header
        int skipped = 0;
        while ((line = br.readLine()) != null) {
            String[] f = line.split("\t", -1);
            if (f.length < 10) continue;
            Sig s = new Sig();
            s.id = f[0]; s.target = f[1]; s.resolve = f[2];
            s.io = Integer.parseInt(f[3]); s.opc = Integer.parseInt(f[4]);
            s.tot = Integer.parseInt(f[5]); s.adj = Integer.parseInt(f[6]);
            s.pri = Integer.parseInt(f[7]); s.src = f[8]; s.pat = f[9];
            s.note = f.length > 10 ? f[10] : "";
            // Skip resolutions this offline model cannot reproduce. Symbol* needs
            // GetProcAddress; CallFollow needs to follow the CALL and scan the callee body
            // for a RIP ref — scoring either with the plain RIP model produces meaningless
            // "decoys" (GNAM_V7 is CallFollow and looked like a 4-decoy pattern until this
            // skip was added).
            if (s.resolve.startsWith("Symbol") || s.resolve.equals("CallFollow")) { skipped++; continue; }
            if (!parse(s)) { skipped++; println("SKIP unparsable " + s.id); continue; }
            sigs.add(s);
        }
        br.close();

        // anchor bucket map
        List<List<Sig>> bucket = new ArrayList<>();
        for (int i = 0; i < 256; i++) bucket.add(new ArrayList<Sig>());
        for (Sig s : sigs) bucket.get(s.bytes[s.anchor] & 0xFF).add(s);

        Memory mem = currentProgram.getMemory();
        long execBytes = 0;
        for (MemoryBlock blk : mem.getBlocks()) {
            if (!blk.isExecute() || !blk.isInitialized()) continue;
            long size = blk.getSize();
            if (size > Integer.MAX_VALUE - 8) continue;
            byte[] buf = new byte[(int) size];
            mem.getBytes(blk.getStart(), buf);
            long base = blk.getStart().getOffset();
            execBytes += size;
            println("scanning " + prog + " " + blk.getName() + " size=" + size);
            for (int i = 0; i < buf.length; i++) {
                List<Sig> cand = bucket.get(buf[i] & 0xFF);
                if (cand.isEmpty()) continue;
                for (Sig s : cand) {
                    int start = i - s.anchor;
                    if (start < 0 || start + s.bytes.length > buf.length) continue;
                    boolean ok = true;
                    for (int k = 0; k < s.bytes.length; k++) {
                        int m = s.mask[k] & 0xFF;
                        if (m == 0) continue;
                        if ((buf[start + k] & m) != (s.bytes[k] & 0xFF)) { ok = false; break; }
                    }
                    if (!ok) continue;
                    long matchVA = base + start;
                    long insVA = matchVA + s.io;
                    long direct = 0, deref = 0;
                    int off = (int) (insVA + s.opc - base);
                    if (off >= 0 && off + 4 <= buf.length) {
                        int rel = (buf[off] & 0xFF) | ((buf[off+1] & 0xFF) << 8)
                                | ((buf[off+2] & 0xFF) << 16) | ((buf[off+3] & 0xFF) << 24);
                        direct = insVA + s.tot + rel;
                        try {
                            Address da = toAddr(direct);
                            if (mem.contains(da)) deref = mem.getLong(da);
                        } catch (Throwable t) { }
                    }
                    s.nHits++;
                    if (s.hits.size() < HIT_DETAIL_CAP) s.hits.add(new long[]{matchVA, direct, deref});
                }
            }
        }
        double execMB = execBytes / (1024.0 * 1024.0);

        // A program with no executable bytes is a BROKEN Ghidra import (image base 0000:0000,
        // zero functions). Several supplied projects contain these as duplicates of a good
        // import under the SAME NAME, so writing them would clobber the real results.
        if (execBytes == 0) {
            println("SKIP " + prog + " — no executable bytes (broken import)");
            return;
        }

        // Key outputs by TAG + program + IMAGE BASE. None of the three alone is unique:
        // `FactoryGame-FactoryGame-Win64-Shipping.dll` exists in both the 4.26 and the 5.2
        // projects (tag disambiguates), and Satisfactory v1.2.3.1 holds a good AND a broken
        // import of Core/CoreUObject/Engine under identical names (image base disambiguates).
        // Getting this wrong silently substitutes one engine version's results for another's —
        // exactly the class of error the sweep exists to catch.
        // SANITIZE THE IMAGE BASE TOO. A segmented address stringifies as "0000:0000", and on
        // NTFS everything after a ':' in a path is an ALTERNATE DATA STREAM name — so
        // "...@0000:0000.txt" silently created a 0-byte file "...@0000" carrying the real
        // content in a hidden stream named "0000.txt". Measured on the Maelstrom and
        // Satisfactory v1.2.3.1 stub re-imports: 0000.tsv = 16,769 bytes, 0000.txt = 14,167
        // bytes, both invisible to a directory listing and to aggregate_sweep.py's glob.
        // Harmless there because those programs map only a 1,104-byte DOS stub and score zero
        // hits — but it is silent DATA LOSS for any real segmented program, and the whole point
        // of keying on the image base (see above) is to stop one binary's results being
        // substituted for another's.
        String stem = sanitize(tag) + "__" + sanitize(prog) + "@"
                    + sanitize(currentProgram.getImageBase().toString());
        String base = outDir + "/scan_" + stem;
        PrintWriter w  = new PrintWriter(new BufferedWriter(new FileWriter(base + ".txt"), 1 << 20));
        PrintWriter t2 = new PrintWriter(new BufferedWriter(new FileWriter(base + ".tsv"), 1 << 18));
        PrintWriter cw = new PrintWriter(new BufferedWriter(new FileWriter(
                outDir + "/consensus_" + stem + ".txt"), 1 << 18));

        w.println("# program " + prog + "  tag=" + tag);
        w.println("# image base " + currentProgram.getImageBase() + "  exec bytes " + execBytes
                  + String.format(" (%.2f MB)", execMB));
        w.println("# scan of " + sigs.size() + " byte patterns (" + skipped + " symbol/unparsable skipped)");
        w.println("# truth: " + truth);
        w.println("# verdict: OK = at least one hit resolves (direct/deref, +adj or raw) to the true VA");
        w.println();
        t2.println("tag\tprog\texec_mb\tid\ttarget\tsrc\tpri\tpat_bytes\tlit_bytes\tlit_nibbles"
                 + "\thits\thits_per_mb\tok\tdecoy\tdistinct_targets\twasted\tverdict\tselected");
        cw.println("# CONSENSUS for " + prog + " — VA -> the DISTINCT patterns that resolve there.");
        cw.println("# Candidate = (direct + adjustment), restricted to non-executable blocks.");
        cw.println("# On a symbol-less binary the top row is the de-facto truth: >=3 independent");
        cw.println("# patterns agreeing was validated on Everspace (matched the PDB exactly).");
        cw.println();

        Set<String> selectedFor = new HashSet<>();
        Map<String, List<Sig>> byTarget = new LinkedHashMap<>();
        for (Sig s : sigs) byTarget.computeIfAbsent(s.target, k -> new ArrayList<Sig>()).add(s);

        for (Map.Entry<String, List<Sig>> e : byTarget.entrySet()) {
            String tgt = e.getKey();
            List<Long> exp = truth.get(tgt);
            w.println("################ TARGET " + tgt + "  true=" + (exp == null ? "?" : exp.toString()));
            List<Sig> lst = e.getValue();
            lst.sort((a, b) -> a.pri - b.pri);

            // LinkedHashMap, NOT HashMap. The consensus listing below sorts by vote count with a
            // STABLE sort, so equal-count rows come out in this map's iteration order — which for
            // a HashMap is bucket order, a function of Long.hashCode's spread and the table's
            // resize history. That is deterministic but reproducible only by emulating
            // java.util.HashMap, it would shift under a JDK upgrade, and it made the file
            // impossible to compare against the pure-Python sweep (pe_scan_patterns.py).
            // First-appearance order is just as arbitrary a tiebreak and is stable everywhere.
            Map<Long, Set<String>> consensus = new LinkedHashMap<>();

            for (Sig s : lst) {
                long nHits = s.nHits;
                int nCorrect = 0, nDecoy = 0, firstCorrectIdx = -1, firstDecoyIdx = -1;
                List<String> okSites = new ArrayList<>();
                Set<String> decoyTargets = new LinkedHashSet<>();
                Set<Long> distinct = new LinkedHashSet<>();
                for (int hi = 0; hi < s.hits.size(); hi++) {
                    long[] h = s.hits.get(hi);
                    long[] cands = { h[1], h[1] + s.adj, h[2], h[2] + s.adj };
                    long primary = h[1] + s.adj;              // the slot the runtime would take
                    if (isDataAddr(mem, primary)) {
                        distinct.add(primary);
                        consensus.computeIfAbsent(primary, k -> new TreeSet<String>()).add(s.id);
                    }
                    boolean thisOk = false;
                    for (int ci = 0; ci < cands.length; ci++) {
                        if (cands[ci] == 0) continue;
                        if (exp != null && exp.contains(cands[ci])) thisOk = true;
                    }
                    if (thisOk) {
                        nCorrect++;
                        if (firstCorrectIdx < 0) firstCorrectIdx = hi;
                        if (okSites.size() < 6) okSites.add(Long.toHexString(h[0]));
                    } else {
                        nDecoy++;
                        if (firstDecoyIdx < 0) firstDecoyIdx = hi;
                        if (decoyTargets.size() < 10) decoyTargets.add(String.format("@%X->%X", h[0], h[1]));
                    }
                }
                // NO-TRUTH is load-bearing, not cosmetic. If you pass a placeholder VA for a
                // target (because the binary has no symbols for it), EVERY hit resolves to
                // something != truth and the old code reported "DECOY-ONLY" — which reads as
                // hard evidence that a pattern misfires. It is not: it means nothing at all.
                // That mistake demoted two perfectly good GEngine patterns once; the tool now
                // refuses to render a decoy verdict without a plausible truth value.
                boolean haveTruth = exp != null;
                if (haveTruth) {
                    boolean plausible = false;
                    for (Long tv : exp) if (tv != null && tv > 0x10000L) plausible = true;
                    haveTruth = plausible;
                }
                String verdict = nHits == 0 ? "MISS      "
                        : !haveTruth ? "NO-TRUTH  "
                        : nCorrect == 0 ? "DECOY-ONLY"
                        : nDecoy == 0 ? "UNIQUE-OK "
                        : (firstCorrectIdx < firstDecoyIdx ? "OK-FIRST  " : "OK-BEHIND ");
                if (!haveTruth) { nCorrect = 0; nDecoy = 0; }
                String truncNote = s.nHits > s.hits.size()
                        ? "  [detail capped at " + s.hits.size() + "]" : "";
                w.println(String.format("%-18s pri=%-4d lit=%-3d hits=%-6d ok=%-5d decoy=%-5d %s src=%s%s",
                        s.id, s.pri, s.litBytes, nHits, nCorrect, nDecoy, verdict, s.src, truncNote));
                if (!okSites.isEmpty()) w.println("        true@ " + okSites);
                if (!decoyTargets.isEmpty()) w.println("        decoy " + decoyTargets
                        + (nDecoy > 10 ? "  ...(" + nDecoy + " total)" : ""));

                // REGRESSION LINE — the question that actually matters when patterns are
                // ADDED to the database: walking priority order, does the FIRST pattern that
                // hits resolve correctly? Genau::ScanForTarget validates each match and takes
                // the first that passes, so a newly-added lower-numbered pattern can only
                // cause harm if it hits AND its match survives validation AND it is wrong.
                boolean isSelected = false;
                if (haveTruth && nHits > 0 && selectedFor.add(tgt)) {
                    isSelected = true;
                    String how;
                    // Respect the ACTUAL scan order. .text is swept low->high, so what matters
                    // is whether the first correct match sits before the first decoy — not
                    // merely whether decoys exist. (This message used to say "decoys scan
                    // first" whenever nDecoy>0, which mis-flagged SPARSE_SP57_1 on Solarpunk
                    // as risky when its correct site is in fact the first match.)
                    if (nCorrect == 0) {
                        how = "  => *** WOULD RESOLVE WRONG unless the validator rejects all "
                              + nDecoy + " hits ***";
                    } else if (nDecoy == 0) {
                        how = "  => CORRECT (all hits)";
                    } else if (firstCorrectIdx < firstDecoyIdx) {
                        how = "  => CORRECT first (" + nDecoy + " decoy(s) scan later, never reached)";
                    } else {
                        // Every match ahead of the first correct one is a decoy, so the count
                        // the validator must survive is firstCorrectIdx (an index, not
                        // firstDecoyIdx — which is just where the first decoy happens to sit).
                        how = "  => AT RISK: " + firstCorrectIdx + " decoy(s) scan BEFORE the "
                              + "first correct match — the validator has to reject every one";
                    }
                    w.println("  >>> SELECTED (first hitting pattern by priority): " + s.id + how);
                }

                // `wasted` = matches the runtime validator must chew through before it reaches a
                // correct one. THIS, not the raw hit count, is the real per-pattern cost.
                long wasted = nHits == 0 ? 0
                            : !haveTruth ? nHits
                            : firstCorrectIdx < 0 ? nHits : firstCorrectIdx;
                t2.println(String.format("%s\t%s\t%.2f\t%s\t%s\t%s\t%d\t%d\t%d\t%d\t%d\t%.1f\t%d\t%d\t%d\t%d\t%s\t%s",
                        tag, prog, execMB, s.id, s.target, s.src, s.pri, s.patLen, s.litBytes,
                        s.litNibbles, nHits, execMB > 0 ? nHits / execMB : 0.0,
                        nCorrect, nDecoy, distinct.size(), wasted, verdict.trim(),
                        isSelected ? "SELECTED" : ""));
            }
            w.println();

            // ---- consensus table for this target ----
            List<Map.Entry<Long, Set<String>>> ce = new ArrayList<>(consensus.entrySet());
            ce.sort((a, b) -> b.getValue().size() - a.getValue().size());
            cw.println("======== TARGET " + tgt + "  true=" + (exp == null ? "?" : exp.toString()));
            int shown = 0;
            for (Map.Entry<Long, Set<String>> c : ce) {
                if (c.getValue().size() < 2 && shown >= 6) break;
                String mark = (exp != null && exp.contains(c.getKey())) ? "  <== TRUE" : "";
                cw.println(String.format("  %-12X  n=%-3d  %s%s",
                        c.getKey(), c.getValue().size(), c.getValue(), mark));
                if (++shown >= 60) { cw.println("  ...(capped)"); break; }
            }

            // ---- PRIORITY WALK ----
            // On a symbol-less binary there is no >>> SELECTED line (withheld on purpose), yet
            // "what would the runtime actually pick here?" is still the question that matters —
            // e.g. for the SquareEnix titles, where a generic pattern hitting is not the same as
            // it hitting RIGHT. This walks the same order Genau::ScanForTarget does and prints
            // each hitting pattern's resolved VA(s), so the first line IS the runtime's answer
            // and it can be compared against the consensus winner above.
            cw.println("  -- priority walk (first hitting patterns, in scan order) --");
            int walked = 0;
            for (Sig s : lst) {
                if (s.nHits == 0) continue;
                Set<Long> vas = new LinkedHashSet<>();
                for (long[] h : s.hits) {
                    long primary = h[1] + s.adj;
                    if (isDataAddr(mem, primary)) vas.add(primary);
                    if (vas.size() >= 4) break;
                }
                StringBuilder sb = new StringBuilder();
                for (Long v : vas) {
                    Set<String> agree = consensus.get(v);
                    sb.append(String.format(" %X(n=%d)", v, agree == null ? 0 : agree.size()));
                }
                cw.println(String.format("   pri=%-4d %-18s hits=%-6d ->%s%s",
                        s.pri, s.id, s.nHits, sb,
                        vas.size() >= 4 ? " ..." : (vas.isEmpty() ? " <none in .data>" : "")));
                if (++walked >= 10) { cw.println("   ...(walk capped)"); break; }
            }
            cw.println();
        }
        w.close();
        t2.close();
        cw.close();
        println("scan_patterns DONE -> " + base + ".txt / .tsv / consensus");
    }
}
