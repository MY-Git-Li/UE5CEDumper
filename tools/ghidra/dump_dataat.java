// dump_dataat.java — for each "Label@hexVA", print the defined Data at that address, its
// DataType name, and the type expanded recursively (depth 4). Also dumps raw bytes.
// Writes out/dataat.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class dump_dataat extends GhidraScript {
    PrintWriter w;
    Set<String> seen = new HashSet<>();

    void expand(DataType dt, String prefix, int depth) {
        if (dt == null || depth > 4) return;
        String key = depth + "|" + prefix + "|" + dt.getPathName();
        if (!seen.add(key)) return;
        if (dt instanceof TypeDef) { w.println(prefix + "typedef -> " + ((TypeDef) dt).getBaseDataType().getName());
            expand(((TypeDef) dt).getBaseDataType(), prefix + "  ", depth + 1); return; }
        if (dt instanceof Pointer) { w.println(prefix + "ptr -> " + ((Pointer) dt).getDataType());
            expand(((Pointer) dt).getDataType(), prefix + "  ", depth + 1); return; }
        if (!(dt instanceof Structure)) { w.println(prefix + dt.getName() + " (" + dt.getClass().getSimpleName()
                + ", size=" + dt.getLength() + ")"); return; }
        Structure s = (Structure) dt;
        w.println(prefix + "struct " + s.getName() + "  size=0x" + Integer.toHexString(s.getLength()));
        for (DataTypeComponent c : s.getDefinedComponents()) {
            DataType f = c.getDataType();
            w.println(String.format("%s  +0x%-4X %-6d %-36s %s", prefix, c.getOffset(), c.getLength(),
                    c.getFieldName() == null ? "<anon>" : c.getFieldName(), f == null ? "?" : f.getName()));
            if (depth < 4) expand(f, prefix + "      ", depth + 1);
        }
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/dataat.txt"), 1 << 20));
        Memory mem = currentProgram.getMemory();
        Listing lst = currentProgram.getListing();
        for (String spec : getScriptArgs()) {
            String[] p = spec.split("@");
            Address a = toAddr(Long.parseLong(p[1], 16));
            w.println("\n=== " + p[0] + " @ " + a + " ===");
            Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(a);
            if (s != null) {
                w.println("symbol: " + s.getName(true) + "  type=" + s.getSymbolType());
                for (Symbol o : currentProgram.getSymbolTable().getSymbols(a)) w.println("  alias: " + o.getName(true));
            }
            Data d = lst.getDataAt(a);
            if (d == null) d = lst.getDataContaining(a);
            if (d != null) {
                DataType dt = d.getDataType();
                w.println("data: " + d.getPathName() + "  len=" + d.getLength() + "  dt=" + (dt == null ? "?" : dt.getPathName()));
                seen.clear();
                expand(dt, "  ", 0);
            } else {
                w.println("data: <undefined>");
            }
            byte[] raw = new byte[128];
            try {
                mem.getBytes(a, raw);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < raw.length; i++) {
                    if (i % 16 == 0) sb.append(String.format("%n  +%02X: ", i));
                    sb.append(String.format("%02X ", raw[i] & 0xFF));
                }
                w.println("raw:" + sb);
            } catch (Exception e) { w.println("raw: <unreadable>"); }
        }
        w.close();
        println("dump_dataat DONE -> " + outDir + "/dataat.txt");
    }
}
