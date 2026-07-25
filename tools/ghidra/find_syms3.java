// find_syms3.java — regex symbol search with a block filter and a demangled-name view.
// Args: <blockFilter: any|data|text> <regex...>   Writes out/symbols3.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;
import java.util.regex.*;

public class find_syms3 extends GhidraScript {
    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        String[] args = getScriptArgs();
        String mode = args.length > 0 ? args[0] : "any";
        List<Pattern> pats = new ArrayList<>();
        for (int i = 1; i < args.length; i++) pats.add(Pattern.compile(args[i]));
        PrintWriter w = new PrintWriter(new BufferedWriter(
                new FileWriter(outDir + "/symbols3.txt"), 1 << 20));
        w.println("# mode=" + mode + " regexes=" + pats);
        SymbolTable st = currentProgram.getSymbolTable();
        Memory mem = currentProgram.getMemory();
        SymbolIterator it = st.getAllSymbols(false);
        int n = 0;
        while (it.hasNext()) {
            Symbol s = it.next();
            SymbolType t = s.getSymbolType();
            if (t == SymbolType.LOCAL_VAR || t == SymbolType.PARAMETER) continue;
            String nm = s.getName();
            if (nm.startsWith("?") || nm.startsWith("__imp")) continue;   // skip raw mangled dupes
            boolean hit = false;
            for (Pattern p : pats) if (p.matcher(nm).find()) { hit = true; break; }
            if (!hit) continue;
            Address a;
            try { a = s.getAddress(); } catch (Throwable e) { continue; }
            if (a == null || !a.isMemoryAddress()) continue;
            MemoryBlock b = mem.getBlock(a);
            if (b == null) continue;
            if (mode.equals("data") && b.isExecute()) continue;
            if (mode.equals("text") && !b.isExecute()) continue;
            String full = nm;
            try { full = s.getName(true); } catch (Throwable e) {}
            w.println(a + "\t" + b.getName() + "\t" + t + "\t" + full);
            if (++n >= 6000) { w.println("...capped"); break; }
        }
        w.close();
        println("find_syms3: matched=" + n);
    }
}
