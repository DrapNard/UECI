using UnrealBuildTool;

public class UECIMinimal : ModuleRules
{
    public UECIMinimal(ReadOnlyTargetRules Target) : base(Target)
    {
        PrivateDependencyModuleNames.Add("Core");
    }
}
