// @category GameHelper
import com.google.gson.Gson;
import com.google.gson.JsonParser;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Function;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Set;

public class InspectFrameworkCoverage extends GhidraScript {
    record Coverage(String signature, String instruction, String function,
                    int targetReferences, List<String> functionEntryInstructions) {}
    record AnchorCoverage(String signature, int definedMatches, int incomingReferences, int callers,
                          int recipeReachableReferences, int sameFunctionReferences) {}

    @Override
    protected void run() throws Exception {
        var report = JsonParser.parseString(Files.readString(Path.of(getScriptArgs()[0]))).getAsJsonObject();
        if (!report.get("sha256").getAsString().equalsIgnoreCase(currentProgram.getExecutableSHA256())) {
            throw new IllegalArgumentException("Signature evidence is for a different client copy");
        }
        var imageBase = currentProgram.getImageBase();
        var gson = new Gson();
        var anchors = new LinkedHashMap<String, List<Address>>();
        anchors.put("Game States", new ArrayList<>());
        anchors.put("File Root", new ArrayList<>());
        anchors.put("AreaChangeCounter", new ArrayList<>());
        var data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            monitor.checkCancelled();
            var item = data.next();
            if (!(item.getValue() instanceof String value)) { continue; }
            if (value.contains("InGameState")) { anchors.get("Game States").add(item.getAddress()); }
            var filename = value.replace('\\', '/');
            if (filename.equals("Mods.dat") || filename.endsWith("/Mods.dat")) {
                anchors.get("File Root").add(item.getAddress());
            }
            if (value.contains("Instance Details from login server")) {
                anchors.get("AreaChangeCounter").add(item.getAddress());
            }
        }
        for (var item : report.getAsJsonArray("patterns")) {
            var pattern = item.getAsJsonObject();
            var candidates = pattern.getAsJsonArray("Candidates");
            if (candidates.size() != 1) {
                continue;
            }
            var row = candidates.get(0).getAsJsonObject();
            if (!row.get("TargetInImage").getAsBoolean()) { continue; }
            Address displacement = imageBase.add(row.get("DisplacementRva").getAsLong());
            Address target = imageBase.add(row.get("TargetRva").getAsLong());
            Instruction instruction = currentProgram.getListing().getInstructionContaining(displacement);
            Function function = getFunctionContaining(displacement);
            var entries = new ArrayList<String>();
            if (function != null) {
                var instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
                while (entries.size() < 8 && instructions.hasNext()) {
                    entries.add(instructions.next().toString());
                }
            }
            int references = 0;
            var incoming = currentProgram.getReferenceManager().getReferencesTo(target);
            while (references < 10000 && incoming.hasNext()) {
                incoming.next();
                references++;
            }
            String name = pattern.get("Name").getAsString();
            var coverage = new Coverage(name,
                instruction == null ? null : instruction.toString(),
                function == null ? null : function.getEntryPoint().toString(), references, entries);
            println("COVERAGE " + gson.toJson(coverage));
            if (anchors.containsKey(name)) {
                println("ANCHOR " + gson.toJson(checkAnchors(name, anchors.get(name), function)));
            }
        }
    }

    private AnchorCoverage checkAnchors(String name, List<Address> strings, Function knownFunction) {
        int references = 0;
        int reaches = 0;
        int same = 0;
        var callers = new HashSet<Address>();
        for (var address : strings) {
            for (var reference : getReferencesTo(address)) {
                references++;
                var site = reference.getFromAddress();
                var caller = getFunctionContaining(site);
                if (caller == null) { continue; }
                callers.add(caller.getEntryPoint());
                if (knownFunction == null) { continue; }
                var target = knownFunction.getEntryPoint();
                if (caller.getEntryPoint().equals(target)) { same++; }
                if (name.equals("AreaChangeCounter")) { continue; }

                var direct = callTargets(caller, name.equals("Game States") ? site : null);
                if (direct.contains(target) || (name.equals("File Root") && direct.stream()
                    .map(this::getFunctionAt).filter(java.util.Objects::nonNull)
                    .anyMatch(function -> callTargets(function, null).contains(target)))) {
                    reaches++;
                }
            }
        }
        return new AnchorCoverage(name, strings.size(), references, callers.size(), reaches, same);
    }

    private Set<Address> callTargets(Function function, Address end) {
        var calls = new HashSet<Address>();
        var instructions = currentProgram.getListing().getInstructions(function.getBody(), true);
        while (instructions.hasNext()) {
            var instruction = instructions.next();
            if (end != null && instruction.getAddress().compareTo(end) >= 0) { break; }
            for (var flow : instruction.getReferencesFrom()) {
                if (flow.getReferenceType().isCall()) { calls.add(flow.getToAddress()); }
            }
        }
        return calls;
    }
}
