// decompile_functions.java — Ghidra headless (Java / Ghidra 12 PyGhidra-free)
//
// Decompiles each function address passed as a script argument and prints the C plus
// every writable-.data global it touches (instr -> target [section]). Forces disassembly /
// createFunction if Ghidra's auto-analysis missed the function. Use it to read off a
// struct's layout from a function that operates on it (e.g. FUObjectArray field offsets
// from AllocateUObjectIndex) — the decompiler is far clearer than raw disasm.
//
// Usage (read-only, won't modify the project):
//   analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
//       -scriptPath <thisDir> -postScript decompile_functions.java 0x147A604E0 0x14814D2F0
//
// Addresses are image-based VAs (e.g. preferred base 0x140000000 + RVA). If no args are
// given it prints the image base and exits.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;

public class decompile_functions extends GhidraScript {
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        println("PROGRAM: " + currentProgram.getName() + "  imageBase=" + currentProgram.getImageBase());

        String[] args = getScriptArgs();
        if (args.length == 0) {
            println("No addresses given. Pass image-based VAs, e.g. -postScript decompile_functions.java 0x147A604E0");
            return;
        }

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        for (String a : args) {
            long va;
            try { va = Long.parseUnsignedLong(a.replaceFirst("^0[xX]", ""), 16); }
            catch (NumberFormatException e) { println("skip bad address: " + a); continue; }
            Address addr = toAddr(va);
            Function f = getFunctionContaining(addr);
            if (f == null) {
                println("(no function at " + addr + " — disassembling + creating)");
                disassemble(addr);
                f = createFunction(addr, null);
            }
            if (f == null) { println("\n==== " + addr + ": could not create function ===="); continue; }

            println("\n================ DECOMPILE " + f.getName() + " @ " + f.getEntryPoint() + " ================");
            DecompileResults res = decomp.decompileFunction(f, 120, monitor);
            println(res.decompileCompleted() ? res.getDecompiledFunction().getC()
                                             : ("  <decompile failed: " + res.getErrorMessage() + ">"));
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
