// scan_patterns.java — mass-scan a TSV of AOB signatures against every executable block and
// resolve each hit exactly like Genau::TryResolveMatch (RipDirect / RipBoth, with adjustment),
// classifying it against the known-true VA for that target.
//
// Supports FULL-byte `??` AND NIBBLE wildcards (`4?` / `?5`) — same semantics as
// Macht::ParsePattern (per-byte AND mask 0x00/0x0F/0xF0/0xFF, bytes pre-masked).
//
// Single pass over .text with an anchor-byte bucket map, so all ~140 patterns cost one sweep.
//
// env GS_TSV   = path to patterns.tsv (id target resolve io opc tot adj pri src pattern note)
// env GS_TRUE  = "GObjects=14a3aa670,GNames=14a363940,GWorld=14a52ced8,SparseDelegates=149ec0910"
// env GS_OUT   = output dir  -> scan_patterns.txt
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
        List<long[]> hits = new ArrayList<>();   // {matchVA, directTarget, derefTarget}
    }

    static boolean parse(Sig s) {
        String[] t = s.pat.trim().split("\\s+");
        s.bytes = new byte[t.length];
        s.mask = new byte[t.length];
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
        }
        for (int i = 0; i < s.mask.length; i++)
            if ((s.mask[i] & 0xFF) == 0xFF) { s.anchor = i; break; }
        return s.anchor >= 0;
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        String tsv = System.getenv("GS_TSV");
        String trueSpec = System.getenv("GS_TRUE");
        // GS_TRUE = "Target=va[|va2|...],Target2=va"  (multiple accepted VAs per target)
        Map<String, List<Long>> truth = new LinkedHashMap<>();
        if (trueSpec != null) for (String kv : trueSpec.split(",")) {
            String[] p = kv.split("=");
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
            if (s.resolve.startsWith("Symbol")) { skipped++; continue; }
            if (!parse(s)) { skipped++; println("SKIP unparsable " + s.id); continue; }
            sigs.add(s);
        }
        br.close();

        // anchor bucket map
        List<List<Sig>> bucket = new ArrayList<>();
        for (int i = 0; i < 256; i++) bucket.add(new ArrayList<Sig>());
        for (Sig s : sigs) bucket.get(s.bytes[s.anchor] & 0xFF).add(s);

        Memory mem = currentProgram.getMemory();
        for (MemoryBlock blk : mem.getBlocks()) {
            if (!blk.isExecute() || !blk.isInitialized()) continue;
            long size = blk.getSize();
            if (size > Integer.MAX_VALUE - 8) continue;
            byte[] buf = new byte[(int) size];
            mem.getBytes(blk.getStart(), buf);
            long base = blk.getStart().getOffset();
            println("scanning " + blk.getName() + " size=" + size);
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
                    if (s.hits.size() < 40000) s.hits.add(new long[]{matchVA, direct, deref});
                }
            }
        }

        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/scan_patterns.txt"), 1 << 20));
        w.println("# scan of " + sigs.size() + " byte patterns (" + skipped + " symbol/unparsable skipped)");
        w.println("# truth: " + truth);
        w.println("# verdict: OK = at least one hit resolves (direct/deref, +adj or raw) to the true VA");
        w.println();
        Map<String, List<Sig>> byTarget = new LinkedHashMap<>();
        for (Sig s : sigs) byTarget.computeIfAbsent(s.target, k -> new ArrayList<Sig>()).add(s);
        for (Map.Entry<String, List<Sig>> e : byTarget.entrySet()) {
            String tgt = e.getKey();
            List<Long> exp = truth.get(tgt);
            w.println("################ TARGET " + tgt + "  true=" + (exp == null ? "?" : exp.toString()));
            List<Sig> lst = e.getValue();
            lst.sort((a, b) -> a.pri - b.pri);
            for (Sig s : lst) {
                int nHits = s.hits.size();
                int nCorrect = 0, nDecoy = 0, firstCorrectIdx = -1, firstDecoyIdx = -1;
                List<String> okSites = new ArrayList<>();
                Set<String> decoyTargets = new LinkedHashSet<>();
                for (int hi = 0; hi < s.hits.size(); hi++) {
                    long[] h = s.hits.get(hi);
                    long[] cands = { h[1], h[1] + s.adj, h[2], h[2] + s.adj };
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
                String verdict = nHits == 0 ? "MISS      "
                        : nCorrect == 0 ? "DECOY-ONLY"
                        : nDecoy == 0 ? "UNIQUE-OK "
                        : (firstCorrectIdx < firstDecoyIdx ? "OK-FIRST  " : "OK-BEHIND ");
                w.println(String.format("%-18s pri=%-4d hits=%-5d ok=%-5d decoy=%-5d %s src=%s",
                        s.id, s.pri, nHits, nCorrect, nDecoy, verdict, s.src));
                if (!okSites.isEmpty()) w.println("        true@ " + okSites);
                if (!decoyTargets.isEmpty()) w.println("        decoy " + decoyTargets
                        + (nDecoy > 10 ? "  ...(" + nDecoy + " total)" : ""));
            }
            w.println();
        }
        w.close();
        println("scan_patterns DONE -> " + outDir + "/scan_patterns.txt");
    }
}
