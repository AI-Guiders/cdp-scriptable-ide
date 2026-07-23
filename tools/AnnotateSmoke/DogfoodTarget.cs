namespace AnnotateDogfood;

public sealed class Target
{

    /// <summary>
    /// Greets or anon.
    /// </summary>
    /// <param name="name">Who to greet</param>
    /// <returns>Greeting string</returns>
    public string Ping(string name)
    {

        // why: empty name -> anon
        if (string.IsNullOrEmpty(name))
            return "anon";
        return "hi " + name;
    }
}
