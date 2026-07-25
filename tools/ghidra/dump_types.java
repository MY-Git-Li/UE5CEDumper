// dump_types.java — dump PDB struct/class layouts (sizeof + every field offset) for the
// UE types the dumper hardcodes or infers. This is the ground truth for corroborating
// docs/technical-notes.md offsets on a real UE 4.27 build.
// Writes out/types.txt.  Args: extra type names to dump (optional).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.data.*;
import java.io.*;
import java.util.*;

public class dump_types extends GhidraScript {

    static final String[] WANT = {
        // --- core UObject spine ---
        "UObjectBase", "UObjectBaseUtility", "UObject", "UField", "UStruct", "UClass",
        "UFunction", "UEnum", "UScriptStruct", "UPackage", "UDynamicClass",
        // --- FField / FProperty (UE 4.25+) ---
        "FField", "FFieldClass", "FFieldVariant", "FProperty",
        "FNumericProperty", "FBoolProperty", "FObjectPropertyBase", "FObjectProperty",
        "FClassProperty", "FWeakObjectProperty", "FLazyObjectProperty", "FSoftObjectProperty",
        "FSoftClassProperty", "FInterfaceProperty", "FNameProperty", "FStrProperty",
        "FTextProperty", "FArrayProperty", "FMapProperty", "FSetProperty", "FStructProperty",
        "FDelegateProperty", "FMulticastDelegateProperty", "FMulticastInlineDelegateProperty",
        "FMulticastSparseDelegateProperty", "FEnumProperty", "FByteProperty", "FIntProperty",
        "FInt8Property", "FInt16Property", "FInt64Property", "FUInt16Property", "FUInt32Property",
        "FUInt64Property", "FFloatProperty", "FDoubleProperty", "FFieldPathProperty",
        // legacy UProperty (pre-4.25 — should NOT exist on 4.27, absence is itself a datapoint)
        "UProperty", "UBoolProperty", "UObjectProperty", "UStructProperty",
        // --- object array ---
        "FUObjectArray", "FUObjectItem", "FChunkedFixedUObjectArray", "FFixedUObjectArray",
        "FUObjectCreateListener", "FUObjectDeleteListener", "FUObjectArray::TIterator",
        // --- names ---
        "FName", "FNameEntry", "FNameEntryHeader", "FNameEntryId", "FNamePool",
        "FNameEntryAllocator", "FNameSlot", "FNameValue", "FNameEntryHandle",
        "FNameStringView", "FNameDebugVisualizer",
        // --- strings/text ---
        "FString", "FText", "FTextData", "FTextHistory", "FTextConstDisplayStringPtr",
        // --- delegates / sparse ---
        "FSparseDelegateStorage", "FSparseDelegate", "FObjectKey", "FWeakObjectPtr",
        "FScriptDelegate", "FMulticastScriptDelegate", "FDelegateHandle",
        "TScriptDelegate", "TMulticastScriptDelegate",
        // --- world / engine spine ---
        "UWorld", "ULevel", "AActor", "APawn", "ACharacter", "AController",
        "APlayerController", "APlayerCameraManager", "AWorldSettings", "AGameStateBase",
        "APlayerState", "UGameInstance", "UEngine", "UGameEngine", "FWorldContext",
        "UCharacterMovementComponent", "UMovementComponent", "USceneComponent",
        "UActorComponent", "ULocalPlayer", "UPlayer", "UGameViewportClient",
        "UWorldProxy", "FHitResult", "FActorSpawnParameters",
        // --- misc containers ---
        "FScriptArray", "FScriptSet", "FScriptMap", "FScriptSparseArray",
        "FHeapAllocator", "FDefaultSetAllocator", "FSetElementId",
        "FVector", "FVector2D", "FRotator", "FTransform", "FQuat", "FMinimalViewInfo",
        // --- reflection helpers ---
        "FImplementedInterface", "FNativeFunctionLookup", "FFrame", "FOutParmRec",
        "FClassBaseChain", "FStructBaseChain", "FRepRecord",
    };

    void dumpOne(PrintWriter w, DataType dt) {
        String cat = dt.getCategoryPath() == null ? "/" : dt.getCategoryPath().getPath();
        if (!(dt instanceof Structure)) {
            w.println("TYPE\t" + dt.getName() + "\tkind=" + dt.getClass().getSimpleName()
                      + "\tsize=" + dt.getLength() + "\tcat=" + cat);
            return;
        }
        Structure s = (Structure) dt;
        w.println("TYPE\t" + s.getName() + "\tkind=struct\tsize=0x"
                  + Integer.toHexString(s.getLength()) + " (" + s.getLength() + ")\tcat=" + cat);
        for (DataTypeComponent c : s.getDefinedComponents()) {
            DataType fdt = c.getDataType();
            String fn = c.getFieldName();
            w.println(String.format("  +0x%-5X %-6d %-40s %s",
                    c.getOffset(), c.getLength(),
                    (fn == null ? "<unnamed>" : fn),
                    (fdt == null ? "?" : fdt.getName())));
        }
        w.println("ENDTYPE");
    }

    public void run() throws Exception {
        String outDir = System.getenv("GS_OUT");
        if (outDir == null) outDir = ".";
        new File(outDir).mkdirs();
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(outDir + "/types.txt"), 1 << 20));
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        w.println("# PDB struct layouts — " + currentProgram.getName()
                  + "  base=" + currentProgram.getImageBase());
        w.println("# total datatypes in manager: " + dtm.getDataTypeCount(true));

        List<String> names = new ArrayList<>(Arrays.asList(WANT));
        for (String extra : getScriptArgs()) names.add(extra);

        int found = 0, miss = 0;
        for (String nm : names) {
            ArrayList<DataType> list = new ArrayList<>();
            try { dtm.findDataTypes(nm, list); } catch (Throwable t) { }
            if (list.isEmpty()) { w.println("MISSING\t" + nm); miss++; continue; }
            int emitted = 0;
            for (DataType dt : list) {
                try { dumpOne(w, dt); } catch (Throwable t) { w.println("ERR\t" + nm + "\t" + t); }
                if (++emitted >= 4) { w.println("...\t" + nm + " has " + list.size() + " matches, capped"); break; }
            }
            found++;
        }
        w.close();
        println("dump_types: found=" + found + " missing=" + miss + " -> " + outDir + "/types.txt");
    }
}
