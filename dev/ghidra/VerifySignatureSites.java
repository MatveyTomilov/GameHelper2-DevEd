// Validates PE signature sites in the imported program.
//@category GameHelper
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.scalar.Scalar;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

public class VerifySignatureSites extends GhidraScript {
    record Site(String signature, long displacementRva, long targetRva, String instruction, boolean confirmed) {}
    record Report(String sha256, String language, boolean allConfirmed, List<Site> sites) {}

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2) {
            throw new IllegalArgumentException("Expected <signature report> <Ghidra report>");
        }

        JsonObject report = JsonParser.parseString(Files.readString(Path.of(args[0]))).getAsJsonObject();
        String hash = currentProgram.getExecutableSHA256();
        if (!report.get("sha256").getAsString().equalsIgnoreCase(hash)) {
            throw new IllegalArgumentException("Signature report SHA-256 does not match the imported image");
        }

        var sites = new ArrayList<Site>();
        for (var row : report.getAsJsonArray("patterns")) {
            monitor.checkCancelled();
            var signature = row.getAsJsonObject();
            for (var match : signature.getAsJsonArray("Candidates")) {
                sites.add(verify(signature.get("Name").getAsString(), match.getAsJsonObject()));
            }
        }

        boolean confirmed = report.get("unique").getAsBoolean() &&
            !sites.isEmpty() && sites.stream().allMatch(Site::confirmed);
        var result = new Report(hash, currentProgram.getLanguageID().toString(), confirmed, sites);
        Files.writeString(Path.of(args[1]), new GsonBuilder().setPrettyPrinting().create().toJson(result));
    }

    private Site verify(String name, JsonObject match) {
        var base = currentProgram.getImageBase();
        var start = base.add(match.get("MatchRva").getAsLong());
        long displacementRva = match.get("DisplacementRva").getAsLong();
        long targetRva = match.get("TargetRva").getAsLong();
        var displacement = base.add(displacementRva);
        var bounds = new AddressSet(start, displacement.add(16));
        new DisassembleCommand(start, bounds, true).applyTo(currentProgram, monitor);

        Instruction instruction = currentProgram.getListing().getInstructionContaining(displacement);
        boolean confirmed = match.get("TargetValid").getAsBoolean() && instruction != null &&
            references(instruction, base.add(targetRva));
        return new Site(name, displacementRva, targetRva, instruction == null ? "" : instruction.toString(), confirmed);
    }

    private static boolean references(Instruction instruction, Address target) {
        if (Arrays.stream(instruction.getReferencesFrom()).anyMatch(reference -> target.equals(reference.getToAddress()))) {
            return true;
        }
        for (int index = 0; index < instruction.getNumOperands(); index++) {
            for (Object object : instruction.getOpObjects(index)) {
                if (target.equals(object) ||
                    (instruction.getMnemonicString().equals("LEA") && object instanceof Scalar scalar &&
                        scalar.getUnsignedValue() == target.getUnsignedOffset())) {
                    return true;
                }
            }
        }
        return false;
    }
}
