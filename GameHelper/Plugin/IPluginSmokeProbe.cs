namespace GameHelper.Plugin
{
    /// <summary>Bounded, local diagnostics collected after a plugin's render pass.</summary>
    public interface IPluginSmokeProbe
    {
        object CollectSmokeData();
    }
}
