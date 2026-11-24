// @category GameHelper
import ghidra.app.script.GhidraScript;

public class ConfigureFrameworkAnalysis extends GhidraScript {
    @Override
    protected void run() {
        for (String option : new String[] {
            "Decompiler Switch Analysis",
            "Windows x86 PE RTTI Analyzer",
        }) {
            if (getCurrentAnalysisOptionsAndValues(currentProgram).containsKey(option)) {
                setAnalysisOption(currentProgram, option, "false");
                println("Recipe analysis disables " + option);
            }
            else {
                println("Recipe analysis option unavailable: " + option);
            }
        }
    }
}
