/// <summary>
/// Scratch entry point; progenitor_exec replaces this body for each experiment.
/// </summary>
public static class Snippet
{
    public static object Run()
    {
        Scratch.Log("scratch snippet: no experiment loaded");
        return "ok";
    }
}
