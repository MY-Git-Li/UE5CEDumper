// dump_blocks.java — emit the COMPLETE memory model of a program: image base, every block, and
// an MD5 of each initialized block's bytes.
//
// WHY. `scan_patterns.java` uses exactly four Ghidra APIs (`getImageBase`, `getMemory`,
// `getBlocks`, and per-block `isExecute/isInitialized/getBytes/getStart`) and ZERO analysis APIs.
// So for the sweep a 181 GB Ghidra corpus is nothing but "image base + block map + bytes". If a
// plain PE reader reproduces that, the sweep does not need Ghidra at all and the corpus can be
// carried as the game binaries alone.
//
// This script is the ORACLE for that claim, and it is deliberately tiny: a few KB per program
// instead of a multi-GB byte export. The per-block MD5 is what makes it decisive — matching
// starts and sizes only proves the LAYOUT agrees, while a matching hash proves the BYTES do too.
// Anything a PE reader gets wrong (a rounded section size, a wrong file offset, a missed
// zero-fill) changes the hash.
//
// env GB_OUT = output dir (default ".")
// env GB_TAG = label for the project (defaults to the program name)
//
// OUTPUT — one TSV per PROGRAM, keyed by the same tag__prog@imagebase stem scan_patterns.java
// uses, for the same reasons (a modular project holds several programs, and one project can hold
// a good AND a broken import of the same name — only the image base separates them).
//   blocks_<stem>.tsv   name / start / size / exec / init / read / write / md5
import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.*;
import java.io.*;
import java.security.MessageDigest;

public class dump_blocks extends GhidraScript {

    static String sanitize(String n) { return n.replaceAll("[^A-Za-z0-9._-]", "_"); }

    static String hex(byte[] d) {
        StringBuilder sb = new StringBuilder(d.length * 2);
        for (byte b : d) sb.append(String.format("%02x", b & 0xFF));
        return sb.toString();
    }

    public void run() throws Exception {
        String outDir = System.getenv("GB_OUT");
        if (outDir == null || outDir.isEmpty()) outDir = ".";
        String tag = System.getenv("GB_TAG");
        String prog = currentProgram.getName();
        if (tag == null || tag.isEmpty()) tag = prog;

        Memory mem = currentProgram.getMemory();
        String stem = sanitize(tag) + "__" + sanitize(prog) + "@"
                    + sanitize(currentProgram.getImageBase().toString());
        PrintWriter w = new PrintWriter(new BufferedWriter(
                new FileWriter(outDir + "/blocks_" + stem + ".tsv"), 1 << 16));
        w.println("# program\t" + prog);
        w.println("# tag\t" + tag);
        w.println("# image_base\t" + Long.toHexString(currentProgram.getImageBase().getOffset()));
        w.println("# image_base_str\t" + currentProgram.getImageBase().toString());
        w.println("name\tstart\tsize\texec\tinit\tread\twrite\tmd5");

        // Read each initialized block in CHUNKS. A single byte[] of the block size is what
        // scan_patterns.java does for .text, but .rdata on a modern UE shipping build is of the
        // same order and this script has to hash EVERY block, not just the executable ones —
        // allocating all of them at full size at once is a needless OOM risk on a small -Xmx.
        final int CHUNK = 1 << 22;
        byte[] buf = new byte[CHUNK];
        for (MemoryBlock b : mem.getBlocks()) {
            String md5 = "";
            if (b.isInitialized()) {
                MessageDigest md = MessageDigest.getInstance("MD5");
                long left = b.getSize(), off = 0;
                while (left > 0) {
                    int n = (int) Math.min(left, CHUNK);
                    mem.getBytes(b.getStart().add(off), buf, 0, n);
                    md.update(buf, 0, n);
                    left -= n; off += n;
                }
                md5 = hex(md.digest());
            }
            w.println(String.format("%s\t%x\t%d\t%d\t%d\t%d\t%d\t%s",
                    b.getName(), b.getStart().getOffset(), b.getSize(),
                    b.isExecute() ? 1 : 0, b.isInitialized() ? 1 : 0,
                    b.isRead() ? 1 : 0, b.isWrite() ? 1 : 0, md5));
        }
        w.close();
        println("dump_blocks DONE -> " + outDir + "/blocks_" + stem + ".tsv");
    }
}
