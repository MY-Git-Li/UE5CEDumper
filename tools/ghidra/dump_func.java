// dump_func.java — disassemble + decompile named/addressed functions, and list the
// .data/.rdata globals they reference. Args: "Label@hexVA" or "Class::Method" or "Name".
// Writes out/func_<label>.txt
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class dump_func extends GhidraScript {

    PrintWriter w;

    void dumpOne(String label, Address entry, String outDir) throws Exception {
        String safe = label.replaceAll("[^A-Za-z0-9_.-]", "_");
        w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/func_" + safe + ".txt"), 1 << 20));
        FunctionManager fm = currentProgram.getFunctionManager();
        Function f = fm.getFunctionContaining(entry);
        w.println("FUNC\t" + label + "\t" + entry + "\t" + (f == null ? "<no function>" : f.getName(true)));
        if (f != null) w.println("BODY\t" + f.getBody().getMinAddress() + "-" + f.getBody().getMaxAddress()
                                 + "\tsize=" + f.getBody().getNumAddresses());

        // linear disassembly of up to 400 instructions from entry
        Listing lst = currentProgram.getListing();
        Memory mem = currentProgram.getMemory();
        Address a = entry;
        StringBuilder bytes = new StringBuilder();
        for (int i = 0; i < 400 && a != null; i++) {
            Instruction ins = lst.getInstructionAt(a);
            if (ins == null) break;
            StringBuilder hb = new StringBuilder();
            try {
                byte[] bb = new byte[ins.getLength()];
                mem.getBytes(a, bb);
                for (byte x : bb) hb.append(String.format("%02X ", x & 0xFF));
            } catch (Exception e) {}
            // annotate rip-relative targets with the symbol name at the destination
            StringBuilder ann = new StringBuilder();
            for (Reference r : ins.getReferencesFrom()) {
                Address to = r.getToAddress();
                if (to == null || !mem.contains(to)) continue;
                Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(to);
                MemoryBlock b = mem.getBlock(to);
                ann.append("  ; -> ").append(to)
                   .append(b == null ? "" : " [" + b.getName() + "]")
                   .append(s == null ? "" : " " + s.getName(true));
            }
            w.println(String.format("%s  %-28s %-42s%s", a, hb.toString(), ins.toString(), ann));
            if (f != null && !f.getBody().contains(a)) break;
            a = a.add(ins.getLength());
        }

        // decompile
        if (f != null) {
            DecompInterface di = new DecompInterface();
            try {
                di.openProgram(currentProgram);
                DecompileResults res = di.decompileFunction(f, 90, monitor);
                if (res != null && res.decompileCompleted()) {
                    w.println("\nDECOMP\n" + res.getDecompiledFunction().getC());
                } else {
                    w.println("\nDECOMP <failed: " + (res == null ? "null" : res.getErrorMessage()) + ">");
                }
            } catch (Throwable t) { w.println("\nDECOMP <exception " + t + ">"); }
            finally { di.dispose(); }
        }
        w.close();
        println("dump_func: " + label + " -> func_" + safe + ".txt");
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        SymbolTable st = currentProgram.getSymbolTable();
        Memory mem = currentProgram.getMemory();
        for (String spec : getScriptArgs()) {
            if (spec.contains("@")) {
                String[] p = spec.split("@");
                dumpOne(p[0], toAddr(Long.parseLong(p[1], 16)), outDir);
                continue;
            }
            String cls = null, fn = spec;
            if (spec.contains("::")) { cls = spec.substring(0, spec.indexOf("::")); fn = spec.substring(spec.indexOf("::") + 2); }
            int found = 0;
            SymbolIterator si = st.getSymbols(fn);
            while (si.hasNext() && found < 3) {
                Symbol s = si.next();
                SymbolType t = s.getSymbolType();
                if (t == SymbolType.LOCAL_VAR || t == SymbolType.PARAMETER) continue;
                if (cls != null) {
                    String ns;
                    try { ns = s.getParentNamespace().getName(false); } catch (Throwable e) { continue; }
                    if (!cls.equals(ns)) continue;
                }
                Address a;
                try { a = s.getAddress(); } catch (Throwable e) { continue; }
                if (a == null || !a.isMemoryAddress()) continue;
                MemoryBlock b = mem.getBlock(a);
                if (b == null || !b.isExecute()) continue;
                dumpOne(spec + "_" + found, a, outDir);
                found++;
            }
            if (found == 0) println("dump_func: " + spec + " NOT FOUND");
        }
        println("dump_func DONE");
    }
}
