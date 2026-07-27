// dump_vtables.java — resolve every vftable of the UE spine classes and print each slot's
// byte offset + target function name. Primary goal: the EXACT byte offset of
// UObject::ProcessEvent in a real UE 4.27 vtable (the DLL currently guesses this by version).
// Writes out/vtables.txt.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class dump_vtables extends GhidraScript {

    static final String[] CLASSES = {
        "UObject", "UObjectBase", "UObjectBaseUtility", "UField", "UStruct", "UClass",
        "UFunction", "UEnum", "UScriptStruct", "UPackage",
        "AActor", "APawn", "ACharacter", "AController", "APlayerController",
        "UWorld", "ULevel", "UEngine", "UGameEngine", "UGameInstance",
        "UActorComponent", "USceneComponent", "UCharacterMovementComponent",
        "AWorldSettings", "UPlayer", "ULocalPlayer",
    };
    // Functions whose vtable slot we specifically care about.
    static final String[] WANTFN = {
        "ProcessEvent", "CallFunction", "Serialize", "PostLoad", "BeginDestroy",
        "FinishDestroy", "GetWorld", "Tick", "IsA", "ProcessConsoleExec",
        "GetLifetimeReplicatedProps", "PostInitProperties", "Rename",
    };

    static final int MAX_SLOTS = 400;

    PrintWriter w;

    String fnNameAt(Address a) {
        if (a == null) return null;
        Function f = currentProgram.getFunctionManager().getFunctionAt(a);
        if (f != null) return f.getName(true);
        Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(a);
        if (s != null) return s.getName(true);
        return null;
    }

    void dumpVft(String cls, Address vft) throws Exception {
        Memory mem = currentProgram.getMemory();
        w.println("VFT\t" + cls + "\t" + vft);
        Map<String, String> want = new LinkedHashMap<>();
        for (int i = 0; i < MAX_SLOTS; i++) {
            Address slot = vft.add((long) i * 8);
            long p;
            try { p = mem.getLong(slot); } catch (Exception e) { break; }
            if (p == 0) break;
            Address fa;
            try { fa = toAddr(p); } catch (Throwable t) { break; }
            MemoryBlock b = mem.getBlock(fa);
            if (b == null || !b.isExecute()) break;   // end of vtable
            String nm = fnNameAt(fa);
            w.println(String.format("  [%3d] +0x%-4X %s\t%s", i, i * 8, fa, nm == null ? "?" : nm));
            if (nm != null) {
                for (String k : WANTFN) {
                    if (nm.endsWith("::" + k) || nm.equals(k)) {
                        want.putIfAbsent(k, String.format("+0x%X (slot %d) -> %s", i * 8, i, nm));
                    }
                }
            }
        }
        for (Map.Entry<String, String> e : want.entrySet())
            w.println("KEY\t" + cls + "\t" + e.getKey() + "\t" + e.getValue());
        w.println("ENDVFT\t" + cls);
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/vtables.txt"), 1 << 20));
        w.println("# vtable slot map — " + currentProgram.getName());

        SymbolTable st = currentProgram.getSymbolTable();
        Memory mem = currentProgram.getMemory();

        // Also print the raw addresses of the interesting member functions.
        for (String cls : CLASSES) {
            for (String fn : WANTFN) {
                SymbolIterator si = st.getSymbols(fn);
                while (si.hasNext()) {
                    Symbol s = si.next();
                    SymbolType t = s.getSymbolType();
                    if (t == SymbolType.LOCAL_VAR || t == SymbolType.PARAMETER) continue;
                    String ns;
                    try { ns = s.getParentNamespace().getName(false); } catch (Throwable e) { continue; }
                    if (!cls.equals(ns)) continue;
                    Address a;
                    try { a = s.getAddress(); } catch (Throwable e) { continue; }
                    if (a == null || !a.isMemoryAddress()) continue;
                    MemoryBlock b = mem.getBlock(a);
                    if (b == null || !b.isExecute()) continue;
                    w.println("FUNC\t" + cls + "::" + fn + "\t" + a);
                }
            }
        }

        // vftable data symbols live under the class namespace and are usually named "vftable"
        // (Ghidra PDB) — dump every one we can find for the target classes.
        Set<String> want = new HashSet<>(Arrays.asList(CLASSES));
        String[] vftNames = { "vftable", "vftable_meta_ptr", "`vftable'" };
        for (String vn : vftNames) {
            SymbolIterator si = st.getSymbols(vn);
            while (si.hasNext()) {
                Symbol s = si.next();
                SymbolType t = s.getSymbolType();
                if (t == SymbolType.LOCAL_VAR || t == SymbolType.PARAMETER) continue;
                String ns;
                try { ns = s.getParentNamespace().getName(false); } catch (Throwable e) { continue; }
                if (!want.contains(ns)) continue;
                Address a;
                try { a = s.getAddress(); } catch (Throwable e) { continue; }
                if (a == null || !a.isMemoryAddress()) continue;
                MemoryBlock b = mem.getBlock(a);
                if (b == null || b.isExecute()) continue;
                try { dumpVft(ns + " (" + vn + ")", a); }
                catch (Throwable e) { w.println("VFTERR\t" + ns + "\t" + e); }
            }
        }
        w.close();
        println("dump_vtables done -> " + outDir + "/vtables.txt");
    }
}
