using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;

var audio = new WindowsAudioService();

Console.WriteLine("=== 目前音訊狀態 ===");
var def = audio.GetDefaultOutputDevice();
Console.WriteLine($"預設裝置: {def?.Name} ({def?.Kind})");

foreach (var device in audio.GetActiveOutputDevices())
{
    var volume = audio.TryGetEndpointVolumePercent(device.Id);
    Console.WriteLine($"  {device.Name}: {(volume is null ? "無法讀取端點音量" : $"{volume:0}%")}");
}

Console.WriteLine("\n=== 設定預設裝置端點音量 33% 並讀回 ===");
if (def is not null)
{
    var setSucceeded = audio.TrySetEndpointVolumePercent(def.Id, 33f);
    var readBack = audio.TryGetEndpointVolumePercent(def.Id);
    Console.WriteLine($"設定: {(setSucceeded ? "成功" : "失敗")};讀回: {(readBack is null ? "無法讀取" : $"{readBack:0}%")}");
}
