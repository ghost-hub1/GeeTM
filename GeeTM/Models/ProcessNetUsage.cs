namespace GeeTM.Models;

public class ProcessNetUsage
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
}



