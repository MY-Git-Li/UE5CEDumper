// find_callers.java — Ghidra headless (Java / Ghidra 12)
//
// For each function address passed as an argument, lists its call sites and the
// LEA/MOV RCX (the x64 `this` / first arg) set immediately before each call. This is how
// you recover a STATIC global that a method is invoked on: e.g. the single caller of
// FUObjectArray::AllocateUObjectIndex does `LEA RCX,[GUObjectArray]; call`, so the LEA
// target IS GUObjectArray. A global loaded via `MOV RCX,[rip+X]` (vs LEA) is a heap object
// pointed to by that .data slot — also reported.
//
// Usage (read-only):
//   analyzeHeadless <projLoc> <projName> -process -noanalysis -readOnly \
//       -scriptPath <thisDir> -postScript find_callers.java 0x147A604E0 0x14814D2F0
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.RefType;

public class find_callers extends GhidraScript {
    String blk(Address a) {
        MemoryBlock b = currentProgram.getMemory().getBlock(a);
        return b == null ? "?" : b.getName();
    }
    public void run() throws Exception {
        println("imageBase=" + currentProgram.getImageBase());
        Memory mem = currentProgram.getMemory();
        String[] args = getScriptArgs();
        if (args.length == 0) { println("Pass function VAs, e.g. -postScript find_callers.java 0x147A604E0"); return; }

        for (String a : args) {
            long va;
            try { va = Long.parseUnsignedLong(a.replaceFirst("^0[xX]", ""), 16); }
            catch (NumberFormatException e) { println("skip bad address: " + a); continue; }
            Address fe = toAddr(va);
            println("\n=== callers of " + fe + " ===");
            int n = 0, shown = 0;
            for (Reference ref : getReferencesTo(fe)) {
                if (!ref.getReferenceType().isCall()) continue;
                n++;
                Address callAddr = ref.getFromAddress();
                Instruction ins = getInstructionBefore(callAddr);
                int steps = 0;
                while (ins != null && steps < 25) {
                    String m = ins.toString();
                    if (m.startsWith("LEA RCX,") || m.startsWith("MOV RCX,")) {
                        Address tgt = null;
                        for (Reference rr : ins.getReferencesFrom())
                            if (rr.getToAddress() != null && mem.contains(rr.getToAddress())) tgt = rr.getToAddress();
                        println("  call@" + callAddr + "  RCX<- " + m
                                + (tgt != null ? ("  -> " + tgt + " [" + blk(tgt) + "]") : ""));
                        shown++;
                        break;
                    }
                    if (m.startsWith("CALL")) break;   // RCX clobbered by an earlier call
                    ins = getInstructionBefore(ins.getAddress());
                    steps++;
                }
                if (shown >= 12) break;
            }
            println("  (" + n + " call refs total)");
        }
        println("\nDONE");
    }
}
