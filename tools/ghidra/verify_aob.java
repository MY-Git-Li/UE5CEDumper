// verify_aob.java — SUPERSEDED by scan_patterns.java. Kept because older docs/notes
// point at it. Prefer scan_patterns.java for anything new: it reads a TSV instead of a
// hand-edited array (so extract_patterns.py can feed it the WHOLE Himmel.h), understands
// nibble wildcards (`4?`/`?5`), counts correct-vs-decoy over every hit instead of the
// first few, and — the part that actually decides safety — reports whether a correct hit
// sorts BEFORE its decoys. A weakly-validated target such as SparseDelegates is unsafe
// when a decoy scans first, and this script cannot tell you that.
//
// verify_aob.java — Ghidra headless (Java / Ghidra 12)
// Scan every executable block for each candidate AOB, resolve each hit the way the
// DLL does (ripInsn=match+io; disp32 @ +opc; target=match+io+tot+disp32), and report
// hits vs decoys vs true-target. Mirrors Genau::ScanForTarget exactly — run this on a
// candidate BEFORE adding it to Himmel.h, so you never ship a decoy-prone signature.
//
// TEMPLATE: edit CAND[] with {id, pattern, instrOffset, opcodeLen, totalLen, expectedVA}
// for the target you're validating (expectedVA = the true symbol's image-based VA, e.g.
// from dump_global_xref_aob.java). A good pattern has correct>=1 AND decoys=0 (or, if
// decoys>0, they must sit at HIGHER addresses than every correct hit so the real site
// validates first). Uses full-byte `??` wildcards only — the DLL parser has no nibble
// wildcards. Big-PDB games need a large heap: set _JAVA_OPTIONS=-Xmx16G before running.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.*;
import java.util.*;

public class verify_aob extends GhidraScript {

    // id, pattern, io, opc, tot, expectedVA
    static final Object[][] CAND = {
      {"GWLD_SP57_1_FinishDestroy", "48 39 1D ?? ?? ?? ?? 75 ?? 48 C7 05 ?? ?? ?? ?? 00 00 00 00", 0,3,7, 0x1478cce58L},
      {"GWLD_SP57_2_Tick2C0",       "48 8B 05 ?? ?? ?? ?? ?? 8B ?? ?? 48 39 81 C0 02 00 00",       0,3,7, 0x1478cce58L},
      {"GWLD_SP57_3_PhysicsState",  "48 8B 05 ?? ?? ?? ?? 48 8B B8 98 02 00 00 48 85 FF 74",       0,3,7, 0x1478cce58L},
      {"GWLD_SP57_4_WriteReports",  "48 8B 05 ?? ?? ?? ?? 48 8B F1 48 8D 4D 10 48 8B 50 18",       0,3,7, 0x1478cce58L},
      {"GWLD_SP57_5_GetWorldFromCtx","48 8B 3D ?? ?? ?? ?? EB ?? 48 85 FF 75 ?? 83 FB 01 75",      0,3,7, 0x1478cce58L},
      {"SPARSE_SP57_1_TSetMov",     "48 8B 15 ?? ?? ?? ?? ?? 63 ?? 48 8D 0C 40 48 C1 E1 05 4C 39 ?? 11 74", 0,3,7, 0x1476ca4b0L},
      {"SPARSE_SP57_2_TSetLea",     "48 8D 15 ?? ?? ?? ?? ?? 63 ?? 4C 8D 14 40 49 C1 E2 05",       0,3,7, 0x1476ca4b0L},
      {"SPARSE_SP57_3_TSetMovR8",   "4C 8B 05 ?? ?? ?? ?? 48 63 C3 48 8D 14 40 48 C1 E2 05 4E 39 1C 02 74", 0,3,7, 0x1476ca4b0L},
    };

    byte[] pbytes; byte[] pmask;
    void parse(String pat) {
        String[] t = pat.trim().split("\\s+");
        pbytes = new byte[t.length]; pmask = new byte[t.length];
        for (int i=0;i<t.length;i++){
            if (t[i].equals("??")){pbytes[i]=0;pmask[i]=0;}
            else {pbytes[i]=(byte)Integer.parseInt(t[i],16);pmask[i]=1;}
        }
    }

    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        println("PROGRAM: "+currentProgram.getName()+" imageBase="+currentProgram.getImageBase());
        List<MemoryBlock> exec = new ArrayList<>();
        for (MemoryBlock b: mem.getBlocks()) if (b.isExecute() && b.isInitialized()) exec.add(b);

        for (Object[] c: CAND) {
            String id=(String)c[0]; parse((String)c[1]);
            int io=(int)c[2], opc=(int)c[3], tot=(int)c[4]; long exp=(long)c[5];
            int hits=0, correct=0; List<String> decoys=new ArrayList<>();
            byte[] onlyMasked = new byte[pbytes.length];
            for (MemoryBlock blk: exec) {
                Address start=blk.getStart();
                // use findBytes with mask across the block
                Address a=start;
                while (a!=null) {
                    a = mem.findBytes(a, blk.getEnd(), pbytes, maskBool(), true, monitor);
                    if (a==null) break;
                    hits++;
                    // resolve
                    long match=a.getOffset();
                    long ripIns=match+io;
                    Address dispAt=toAddr(ripIns+opc);
                    int disp=mem.getInt(dispAt);
                    long target=ripIns+tot+((long)disp);
                    if (target==exp) correct++;
                    else if (decoys.size()<8) decoys.add(String.format("@%X->%X",match,target));
                    a=a.add(1);
                }
            }
            println(String.format("%-28s hits=%-3d correct=%-3d decoys=%d  %s",
                id, hits, correct, hits-correct, decoys.isEmpty()?"":decoys.toString()));
        }
        println("DONE");
    }

    // Ghidra findBytes wants a mask where 1 bits = must-match; build per-byte 0x00/0xFF.
    byte[] maskBool(){
        byte[] m=new byte[pmask.length];
        for(int i=0;i<pmask.length;i++) m[i]=(pmask[i]!=0)?(byte)0xFF:0x00;
        return m;
    }
}
