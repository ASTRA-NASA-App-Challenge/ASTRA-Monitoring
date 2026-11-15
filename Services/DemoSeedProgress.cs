namespace MonitoringApi.Services;

// Volatile process status only; no saved demo anchors or sidecar files.
public sealed class DemoSeedProgress
{
    private volatile string phase = "checking";
    public string Phase => phase;
    public void Set(string value) => phase = value;
}
