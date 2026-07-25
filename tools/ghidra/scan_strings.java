// scan_strings.java — raw byte scan of the initialized non-exec blocks for ASCII/UTF-16
// substrings. Args: substrings. Writes out/strings.txt (addr + surrounding text).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

public class scan_strings extends GhidraScript {
    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        String[] args = getScriptArgs();
        if (args.length == 0) args = new String[]{"++UE4+Release-", "++UE5+Release-", "UnrealEngine 4."};
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/strings.txt"), 1 << 20));
        Memory mem = currentProgram.getMemory();
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized() || b.isExecute()) continue;
            long len = b.getSize();
            if (len > 200_000_000L) { w.println("# skip huge block " + b.getName()); continue; }
            byte[] buf = new byte[(int) len];
            try { mem.getBytes(b.getStart(), buf); } catch (Exception e) { w.println("# unreadable " + b.getName()); continue; }
            for (String needle : args) {
                // ASCII
                byte[] pa = needle.getBytes(StandardCharsets.US_ASCII);
                findAll(w, b, buf, pa, "ascii", needle);
                // UTF-16LE
                byte[] pw = needle.getBytes(StandardCharsets.UTF_16LE);
                findAll(w, b, buf, pw, "utf16", needle);
            }
        }
        w.close();
        println("scan_strings DONE -> " + outDir + "/strings.txt");
    }

    void findAll(PrintWriter w, MemoryBlock b, byte[] buf, byte[] pat, String kind, String needle) {
        int hits = 0;
        outer:
        for (int i = 0; i + pat.length <= buf.length; i++) {
            for (int j = 0; j < pat.length; j++) if (buf[i + j] != pat[j]) continue outer;
            Address a = b.getStart().add(i);
            int end = Math.min(buf.length, i + pat.length + 80);
            StringBuilder sb = new StringBuilder();
            for (int k = i; k < end; k++) {
                byte c = buf[k];
                if (c == 0) { if (kind.equals("utf16")) continue; else break; }
                sb.append((c >= 32 && c < 127) ? (char) c : '.');
            }
            w.println(kind + "\t" + b.getName() + "\t" + a + "\t" + needle + "\t" + sb);
            if (++hits >= 40) { w.println("# capped " + needle + " " + kind); break; }
        }
    }
}
