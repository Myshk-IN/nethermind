using Grandine;

public class GrandineIntegration
{
    private readonly Grandine _grandine;

    public GrandineIntegration()
    {
        _grandine = new Grandine();
    }

    public void Start(string[] args)
    {
        _grandine.Run(args);
    }
}
