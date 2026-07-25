// pe_probe.java — exact simulation of dll/src/Frieren.cpp DetectProcessEventVTableOffsetByPattern
// against real vtables from this binary. Answers: on a genuine UE 4.27 build, does the pattern
// scanner find ProcessEvent, and at which byte offset?
//
// Args: vtable VAs in hex (e.g. 14871aa80). Writes out/pe_probe.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class pe_probe extends GhidraScript {

    // exactly the DLL's constants
    static final int MIN_OFF = 0x100, MAX_OFF = 0x300, BODY = 0xF00;
    static final int[] P1 = {0xF7, -1, -1, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00};
    static final int[] P2 = {0xF7, -1, -1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00};

    static boolean contains(byte[] hay, int size, int[] pat) {
        if (size < pat.length) return false;
        outer:
        for (int i = 0; i <= size - pat.length; i++) {
            for (int j = 0; j < pat.length; j++) {
                if (pat[j] < 0) continue;
                if ((hay[i + j] & 0xFF) != pat[j]) continue outer;
            }
            return true;
        }
        return false;
    }

    static int find(byte[] hay, int size, int[] pat) {
        if (size < pat.length) return -1;
        outer:
        for (int i = 0; i <= size - pat.length; i++) {
            for (int j = 0; j < pat.length; j++) {
                if (pat[j] < 0) continue;
                if ((hay[i + j] & 0xFF) != pat[j]) continue outer;
            }
            return i;
        }
        return -1;
    }

    String fname(Address a) {
        Function f = currentProgram.getFunctionManager().getFunctionAt(a);
        if (f != null) return f.getName(true);
        Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(a);
        return s == null ? "?" : s.getName(true);
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/pe_probe.txt")));
        Memory mem = currentProgram.getMemory();
        for (String spec : getScriptArgs()) {
            Address vt = toAddr(Long.parseLong(spec, 16));
            w.println("\n=== VTABLE " + vt + " ===");
            int firstMatch = -1;
            for (int off = MIN_OFF; off <= MAX_OFF; off += 8) {
                long fp;
                try { fp = mem.getLong(vt.add(off)); } catch (Exception e) { continue; }
                if (fp == 0) continue;
                Address fa;
                try { fa = toAddr(fp); } catch (Throwable t) { continue; }
                MemoryBlock b = mem.getBlock(fa);
                if (b == null || !b.isExecute()) continue;
                byte[] body = new byte[BODY];
                int n = BODY;
                try { mem.getBytes(fa, body); }
                catch (Exception e) {
                    // partial read like the DLL's ReadBytesSafe failing => skip
                    w.println(String.format("  +0x%03X  %-60s  <unreadable %d bytes>", off, fname(fa), BODY));
                    continue;
                }
                boolean p1 = contains(body, 0x400, P1);
                boolean p2 = contains(body, n, P2);
                String nm = fname(fa);
                if (p1 || p2 || nm.endsWith("ProcessEvent")) {
                    w.println(String.format("  +0x%03X  %-60s p1=%-5s(@%d) p2=%-5s(@%d) %s",
                            off, nm, p1, find(body, 0x400, P1), p2, find(body, n, P2),
                            (p1 && p2) ? "  <== DLL WOULD PICK THIS" : ""));
                }
                if (p1 && p2 && firstMatch < 0) firstMatch = off;
            }
            w.println("  RESULT: pattern scanner picks offset " +
                      (firstMatch < 0 ? "NONE (falls back to the version table)"
                                      : String.format("0x%X", firstMatch)));
        }
        w.close();
        println("pe_probe DONE -> " + outDir + "/pe_probe.txt");
    }
}
