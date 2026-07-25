// probe.java — confirm the program opened, PDB symbols present, and key UE globals resolve.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;

public class probe extends GhidraScript {
    public void run() throws Exception {
        println("PROG\t" + currentProgram.getName()
                + "\tbase=" + currentProgram.getImageBase()
                + "\texe=" + currentProgram.getExecutablePath());
        println("FORMAT\t" + currentProgram.getExecutableFormat()
                + "\tmd5=" + currentProgram.getExecutableMD5());
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            println("BLOCK\t" + b.getName() + "\t" + b.getStart() + "-" + b.getEnd()
                    + "\tr=" + b.isRead() + " w=" + b.isWrite() + " x=" + b.isExecute()
                    + " init=" + b.isInitialized());
        }
        println("FUNCS\t" + currentProgram.getFunctionManager().getFunctionCount());
        SymbolTable st = currentProgram.getSymbolTable();
        println("SYMS\t" + st.getNumSymbols());
        String[] probes = {
            "GWorld", "GUObjectArray", "GObjects", "NamePoolData", "GNames",
            "SparseDelegates", "GEngine", "GMalloc", "GIsEditor", "GFrameNumber",
            "GameThreadId", "GNameBlocksDebug"
        };
        for (String nm : probes) {
            int n = 0;
            StringBuilder sb = new StringBuilder();
            SymbolIterator it = st.getSymbols(nm);
            while (it.hasNext()) {
                Symbol s = it.next();
                SymbolType t = s.getSymbolType();
                if (t == SymbolType.LOCAL_VAR || t == SymbolType.PARAMETER) continue;
                Address a;
                try { a = s.getAddress(); } catch (Throwable e) { continue; }
                if (a == null || !a.isMemoryAddress()) continue;
                MemoryBlock b = currentProgram.getMemory().getBlock(a);
                String parent = "?";
                try { parent = s.getParentNamespace().getName(true); } catch (Throwable e) {}
                sb.append("\n   ").append(a).append(" blk=").append(b == null ? "?" : b.getName())
                  .append(" type=").append(t).append(" ns=").append(parent);
                if (++n >= 12) { sb.append("\n   ...(capped)"); break; }
            }
            println("PROBE\t" + nm + "\tn=" + n + sb);
        }
        println("DONE");
    }
}
