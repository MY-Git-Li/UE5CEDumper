// dump_xrefs2.java — for each UE global (by name or by "Label@hexVA"), dump every code xref as
//   RAW   : unmasked bytes of the window
//   AOBn  : disp-masked candidate starting n instructions BEFORE the referencing insn
//           (n = 0,2,4,6) together with the instrOffset the DLL needs
//   DIS   : the surrounding disassembly, so a human can name the site
// Writes out/xrefs_<label>.txt (one file per symbol) + out/xrefs_index.txt.
//
// Walking backwards uses Ghidra's already-analyzed instruction listing (getInstructionBefore),
// which is reliable here — the program is fully analyzed.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class dump_xrefs2 extends GhidraScript {

    static final int FWD = 56;          // bytes forward from the referencing instruction
    static final int[] BACK_INSNS = {0, 2, 4, 6};
    static final int MAX_XREFS = 400;

    String hex(byte[] b, int from, int len, Set<Integer> masked) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < len; i++) {
            if (i > 0) sb.append(' ');
            if (masked != null && masked.contains(from + i)) sb.append("??");
            else sb.append(String.format("%02X", b[from + i] & 0xFF));
        }
        return sb.toString();
    }

    // Mask every rip-relative disp32 and every rel32 branch target inside [start, start+span).
    Set<Integer> maskDisps(Address start, int span) throws Exception {
        Set<Integer> masked = new HashSet<>();
        Listing lst = currentProgram.getListing();
        Address a = start;
        long end = start.getOffset() + span;
        int guard = 0;
        byte[] region = new byte[span];
        try { currentProgram.getMemory().getBytes(start, region); } catch (Exception e) { return masked; }
        while (a != null && a.getOffset() < end && guard++ < 64) {
            Instruction ins = lst.getInstructionAt(a);
            if (ins == null) break;
            int len = ins.getLength();
            long next = a.getOffset() + len;
            int insRel = (int) (a.getOffset() - start.getOffset());
            for (Reference r : ins.getReferencesFrom()) {
                Address to = r.getToAddress();
                if (to == null || !currentProgram.getMemory().contains(to)) continue;
                long disp = to.getOffset() - next;
                int d = (int) disp;
                byte b0 = (byte) (d & 0xFF), b1 = (byte) ((d >> 8) & 0xFF),
                     b2 = (byte) ((d >> 16) & 0xFF), b3 = (byte) ((d >> 24) & 0xFF);
                for (int i = 0; i + 3 < len; i++) {
                    int p = insRel + i;
                    if (p < 0 || p + 3 >= span) continue;
                    if (region[p] == b0 && region[p+1] == b1 && region[p+2] == b2 && region[p+3] == b3) {
                        masked.add(p); masked.add(p+1); masked.add(p+2); masked.add(p+3);
                    }
                }
                // rel8 branches (jcc short / jmp short)
                int d8 = (int) disp;
                if (d8 >= -128 && d8 <= 127 && len >= 2) {
                    int p = insRel + len - 1;
                    if (p >= 0 && p < span && region[p] == (byte) d8) masked.add(p);
                }
            }
            a = a.add(len);
        }
        return masked;
    }

    Address backN(Address a, int n) {
        Address cur = a;
        for (int i = 0; i < n; i++) {
            Instruction prev = getInstructionBefore(cur);
            if (prev == null) return cur;
            // must be contiguous — no gap, no cross-function padding
            if (prev.getAddress().add(prev.getLength()).getOffset() != cur.getOffset()) return cur;
            cur = prev.getAddress();
        }
        return cur;
    }

    void dumpSym(String label, Address symAddr, String outDir) throws Exception {
        Memory mem = currentProgram.getMemory();
        FunctionManager fm = currentProgram.getFunctionManager();
        MemoryBlock blk = mem.getBlock(symAddr);
        String safe = label.replaceAll("[^A-Za-z0-9_.-]", "_");
        PrintWriter w = new PrintWriter(new BufferedWriter(
                new FileWriter(outDir + "/xrefs_" + safe + ".txt"), 1 << 20));
        w.println("SYM\t" + label + "\t" + symAddr + "\tblock=" + (blk == null ? "?" : blk.getName()));

        int n = 0;
        for (Reference ref : getReferencesTo(symAddr)) {
            Address from = ref.getFromAddress();
            Instruction ins = getInstructionAt(from);
            if (ins == null) ins = getInstructionContaining(from);
            if (ins == null) continue;
            RefType rt = ref.getReferenceType();
            String kind = rt.isWrite() ? "W" : (rt.isRead() ? "R" : (rt.isData() ? "D" : rt.toString()));
            String fn = "?";
            try { Function f = fm.getFunctionContaining(from); if (f != null) fn = f.getName(true); }
            catch (Throwable t) { fn = "<fnErr>"; }
            Address insAddr = ins.getAddress();

            w.println();
            w.println("XREF\t#" + n + "\t" + insAddr + "\t" + kind + "\t" + ins.toString() + "\tIN " + fn);

            // disassembly context
            Address ds = backN(insAddr, 6);
            StringBuilder dis = new StringBuilder();
            Address c = ds;
            for (int i = 0; i < 18 && c != null; i++) {
                Instruction ci = getInstructionAt(c);
                if (ci == null) break;
                dis.append(String.format("  %s%s  %s%n", c, c.equals(insAddr) ? " *" : "  ", ci.toString()));
                c = c.add(ci.getLength());
                if (c.getOffset() > insAddr.getOffset() + FWD) break;
            }
            w.print("DIS\n" + dis);

            for (int bn : BACK_INSNS) {
                Address start = backN(insAddr, bn);
                int io = (int) (insAddr.getOffset() - start.getOffset());
                int span = io + FWD;
                if (span > 200) continue;
                byte[] region = new byte[span];
                try { mem.getBytes(start, region); } catch (Exception e) { continue; }
                Set<Integer> masked = maskDisps(start, span);
                w.println("AOB" + bn + "\tio=" + io + "\t" + hex(region, 0, span, masked));
                w.println("RAW" + bn + "\tio=" + io + "\t" + hex(region, 0, span, null));
            }
            if (++n >= MAX_XREFS) { w.println("\n(capped at " + MAX_XREFS + ")"); break; }
        }
        w.println("\nSYMEND\t" + label + "\tcount=" + n);
        w.close();
        println("dump_xrefs2: " + label + " @" + symAddr + " xrefs=" + n);
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        String[] args = getScriptArgs();
        SymbolTable st = currentProgram.getSymbolTable();
        PrintWriter idx = new PrintWriter(new FileWriter(outDir + "/xrefs_index.txt"));

        for (String spec : args) {
            if (spec.contains("@")) {
                String[] p = spec.split("@");
                Address a = toAddr(Long.parseLong(p[1], 16));
                idx.println(p[0] + "\t" + a);
                dumpSym(p[0], a, outDir);
                continue;
            }
            boolean any = false;
            SymbolIterator it = st.getSymbols(spec);
            while (it.hasNext()) {
                Symbol s = it.next();
                SymbolType t = s.getSymbolType();
                if (t == SymbolType.LOCAL_VAR || t == SymbolType.PARAMETER) continue;
                Address a;
                try { a = s.getAddress(); } catch (Throwable e) { continue; }
                if (a == null || !a.isMemoryAddress()) continue;
                MemoryBlock b = currentProgram.getMemory().getBlock(a);
                if (b == null || b.isExecute()) continue;
                String ns = "g";
                try { ns = s.getParentNamespace().getName(false); } catch (Throwable e) {}
                any = true;
                idx.println(spec + "|" + ns + "\t" + a);
                dumpSym(spec + "_" + ns, a, outDir);
            }
            if (!any) { idx.println(spec + "\t<not found>"); println("dump_xrefs2: " + spec + " NOT FOUND"); }
        }
        idx.close();
        println("dump_xrefs2 DONE");
    }
}
