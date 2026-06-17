// find_gobjects.java — Ghidra headless (Java / Ghidra 12)
//
// Anchors on the UE log string "Unable to add more objects to disregard for GC pool"
// (which lives in FUObjectArray::AllocateUObjectIndex), finds the functions that
// reference it, and decompiles them + lists the writable-.data globals they touch — so
// you can read off GUObjectArray and the FUObjectArray field layout. Pass a different
// substring as the first arg to anchor on another string.
//
// Usage (read-only):
//   analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
//       -scriptPath <thisDir> -postScript find_gobjects.java
//
// CAVEAT: this relies on Ghidra's auto-analysis having created the code->string
// reference. On large stripped shipping EXEs that reference may be absent (a partial
// analysis won't have it). If this prints "found 0 xrefs", instead resolve the candidate
// functions with patternsleuth's CLI:
//     patternsleuth scan --path game.exe --resolver FUObjectArrayAllocateUObjectIndex --disassemble-merged
// then feed those addresses to decompile_functions.java + find_callers.java.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.util.LinkedHashSet;
import java.util.Set;

public class find_gobjects extends GhidraScript {
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        ReferenceManager refmgr = currentProgram.getReferenceManager();
        FunctionManager fm = currentProgram.getFunctionManager();
        println("PROGRAM: " + currentProgram.getName() + "  imageBase=" + currentProgram.getImageBase());

        String[] args = getScriptArgs();
        String needle = args.length > 0 ? args[0] : "Unable to add more objects to disregard for GC pool";

        // Find the UTF-16LE string in memory.
        byte[] pat = new byte[needle.length() * 2];
        for (int i = 0; i < needle.length(); i++) { pat[i * 2] = (byte) (needle.charAt(i) & 0xFF); pat[i * 2 + 1] = 0; }
        Set<Address> strAddrs = new LinkedHashSet<>();
        Address a = mem.findBytes(mem.getMinAddress(), pat, null, true, monitor);
        while (a != null && strAddrs.size() < 16) { strAddrs.add(a); a = mem.findBytes(a.add(2), pat, null, true, monitor); }
        println("found " + strAddrs.size() + " occurrence(s) of: " + needle);

        // Functions that reference the string (DB refs, then a raw instruction scan fallback).
        Set<Function> funcs = new LinkedHashSet<>();
        for (Address sa : strAddrs)
            for (Reference ref : refmgr.getReferencesTo(sa)) {
                Function f = fm.getFunctionContaining(ref.getFromAddress());
                if (f != null && funcs.add(f)) println("  xref(db) " + ref.getFromAddress() + " -> " + f.getName() + " @ " + f.getEntryPoint());
            }
        if (funcs.isEmpty()) {
            println("(no DB xrefs — scanning instructions...)");
            InstructionIterator all = currentProgram.getListing().getInstructions(true);
            while (all.hasNext() && !monitor.isCancelled()) {
                Instruction ins = all.next();
                for (Reference r : ins.getReferencesFrom())
                    if (strAddrs.contains(r.getToAddress())) {
                        Function f = fm.getFunctionContaining(ins.getAddress());
                        if (f != null && funcs.add(f)) println("  xref(scan) " + ins.getAddress() + " -> " + f.getName() + " @ " + f.getEntryPoint());
                    }
            }
        }
        if (funcs.isEmpty()) { println("found 0 xrefs — see the CAVEAT in this script's header."); return; }

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        for (Function f : funcs) {
            println("\n================ DECOMPILE " + f.getName() + " @ " + f.getEntryPoint() + " ================");
            DecompileResults res = decomp.decompileFunction(f, 90, monitor);
            println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : ("  <decompile failed: " + res.getErrorMessage() + ">"));
            println("---- writable-data globals referenced ----");
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                for (Reference r : ins.getReferencesFrom()) {
                    Address to = r.getToAddress();
                    if (to != null && mem.contains(to)) {
                        MemoryBlock blk = mem.getBlock(to);
                        if (blk != null && blk.isWrite() && !blk.isExecute())
                            println("  " + ins.getAddress() + "  " + ins.toString() + "  -> " + to + " [" + blk.getName() + "]");
                    }
                }
            }
        }
        println("\nDONE");
    }
}
