// dump_global_xref_aob.java — Ghidra headless (Java / Ghidra 12)
//
// For a set of UE global symbols (resolved by name from the loaded PDB), dumps every
// code cross-reference as BOTH:
//   RAW  : 64-byte window (insAddr-16 .. +48) unmasked  -> test existing AOBs against this
//   AOB  : disp-masked candidate pattern from insAddr    -> new signature material
// Also prints the referencing instruction, read/write kind, and containing function.
//
// Output is machine-parseable: lines are prefixed with a tag (SYM/XREF/RAW/AOB/DIS).
//
// Usage (read-only):
//   analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
//       -scriptPath <dir> -postScript dump_global_xref_aob.java [name1 name2 ...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.util.*;

public class dump_global_xref_aob extends GhidraScript {

    static final int SPAN = 40;      // bytes of masked AOB from the instruction start
    static final int RAW_BACK = 16;  // raw window bytes before the instruction
    static final int RAW_FWD = 48;   // raw window bytes after the instruction start

    String hex(byte[] b, int from, int len, Set<Integer> masked) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < len; i++) {
            if (i > 0) sb.append(' ');
            if (masked != null && masked.contains(from + i)) sb.append("??");
            else sb.append(String.format("%02X", b[from + i] & 0xFF));
        }
        return sb.toString();
    }

    // Mask the rip-relative disp32 of every instruction overlapping [start, start+span).
    // Returns set of absolute byte offsets (relative to `base`) to mask.
    Set<Integer> maskDisps(Address start, int span, Address base, byte[] region) throws Exception {
        Set<Integer> masked = new HashSet<>();
        Listing lst = currentProgram.getListing();
        Address a = start;
        long end = start.getOffset() + span;
        int guard = 0;
        while (a != null && a.getOffset() < end && guard++ < 32) {
            Instruction ins = lst.getInstructionAt(a);
            if (ins == null) break;
            int len = ins.getLength();
            long next = a.getOffset() + len;
            // find a rip-relative memory ref target
            for (Reference r : ins.getReferencesFrom()) {
                Address to = r.getToAddress();
                if (to == null || !currentProgram.getMemory().contains(to)) continue;
                if (!(r.getReferenceType().isData() || r.getReferenceType().isRead()
                      || r.getReferenceType().isWrite())) continue;
                long disp = to.getOffset() - next;   // rip-relative to end of full instruction
                int d = (int) disp;
                byte b0=(byte)(d&0xFF), b1=(byte)((d>>8)&0xFF), b2=(byte)((d>>16)&0xFF), b3=(byte)((d>>24)&0xFF);
                // search the instruction bytes for that 4-byte LE value
                int insStartRel = (int)(a.getOffset() - base.getOffset());
                for (int i = 0; i + 3 < len; i++) {
                    int p = insStartRel + i;
                    if (p < 0 || p + 3 >= region.length) continue;
                    if (region[p]==b0 && region[p+1]==b1 && region[p+2]==b2 && region[p+3]==b3) {
                        masked.add(p); masked.add(p+1); masked.add(p+2); masked.add(p+3);
                    }
                }
            }
            a = a.add(len);
        }
        return masked;
    }

    void dumpSym(String label, Address symAddr) throws Exception {
        Memory mem = currentProgram.getMemory();
        FunctionManager fm = currentProgram.getFunctionManager();
        MemoryBlock blk = mem.getBlock(symAddr);
        println("SYM\t" + label + "\t" + symAddr + "\tblock=" + (blk==null?"?":blk.getName())
                + " write=" + (blk!=null&&blk.isWrite()) + " exec=" + (blk!=null&&blk.isExecute()));
        int n = 0;
        for (Reference ref : getReferencesTo(symAddr)) {
            Address from = ref.getFromAddress();
            Instruction ins = getInstructionAt(from);
            if (ins == null) ins = getInstructionContaining(from);
            if (ins == null) continue;
            RefType rt = ref.getReferenceType();
            String kind = rt.isWrite()?"W":(rt.isRead()?"R":(rt.isData()?"D":rt.toString()));
            String fn = "?";
            try { Function f = fm.getFunctionContaining(from); if (f != null) fn = f.getName(); }
            catch (Throwable t) { fn = "<fnErr>"; }
            Address insAddr = ins.getAddress();
            println("XREF\t" + label + "\t" + insAddr + "\t" + kind + "\t" + ins.toString() + "\t" + fn);
            // RAW window
            Address rawBase = insAddr.subtract(RAW_BACK);
            int rawLen = RAW_BACK + RAW_FWD;
            byte[] raw = new byte[rawLen];
            try { mem.getBytes(rawBase, raw); } catch (Exception e) { println("RAW\t<unreadable>"); continue; }
            println("RAW@-16\t" + hex(raw, 0, rawLen, null));
            // Masked AOB from insAddr
            byte[] region = new byte[SPAN];
            try { mem.getBytes(insAddr, region); } catch (Exception e) { println("AOB\t<unreadable>"); n++; continue; }
            Set<Integer> masked = maskDisps(insAddr, SPAN, insAddr, region);
            println("AOB\t" + hex(region, 0, SPAN, masked));
            n++;
            if (n >= 40) { println("XREF\t" + label + "\t(capped at 40)"); break; }
        }
        println("SYMEND\t" + label + "\tcount=" + n);
    }

    public void run() throws Exception {
        println("PROGRAM: " + currentProgram.getName() + "  imageBase=" + currentProgram.getImageBase());
        String[] args = getScriptArgs();
        String[] names = (args.length > 0) ? args : new String[]{
            "GWorld", "GUObjectArray", "GObjects", "NamePoolData", "GNames",
            "SparseDelegates", "GEngine"
        };
        SymbolTable st = currentProgram.getSymbolTable();
        for (String nm : names) {
            boolean any = false;
            SymbolIterator it = st.getSymbols(nm);
            while (it.hasNext()) {
                Symbol s = it.next();
                try {
                    SymbolType stype = s.getSymbolType();
                    // NEVER touch variable symbols — getAddress() lazy-loads the whole
                    // function's variables + datatype list (OOM on a 1.6GB PDB).
                    if (stype == SymbolType.LOCAL_VAR || stype == SymbolType.PARAMETER) continue;
                    Address a = s.getAddress();
                    if (a == null || !a.isMemoryAddress()) continue;
                    MemoryBlock b = currentProgram.getMemory().getBlock(a);
                    if (b == null || b.isExecute()) continue;   // skip code-address symbols (functions)
                    String parent = "?";
                    try { parent = s.getParentNamespace().getName(); } catch (Throwable t) {}
                    any = true;
                    dumpSym(nm + "|" + s.getName() + "|" + parent + "|" + stype, a);
                } catch (Throwable t) {
                    println("SYMSKIP\t" + nm + "\t" + t.getClass().getSimpleName());
                }
            }
            if (!any) println("SYM\t" + nm + "\t<no data symbol found>");
        }
        println("\nDONE");
    }
}
