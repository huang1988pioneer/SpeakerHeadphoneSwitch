using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;

var audio = new WindowsAudioService();

Console.WriteLine("=== 目前音訊狀態 ===");
var def = audio.GetDefaultOutputDevice();
Console.WriteLine($"預設裝置: {def?.Name} ({def?.Kind})");

foreach (var d in audio.GetActiveOutputDevices())
{
    var v = audio.TryGetVolumePercent(d.Id);
    Console.WriteLine($"  {d.Name}: {(v is null ? "無法讀取(端點與工作階段皆失敗)" : $"{v:0}%")}");
}

Console.WriteLine("\n=== 設定預設裝置音量 33% 並讀回 ===");
if (def is not null)
{
    var ok = audio.TrySetVolumePercent(def.Id, 33f);
    var v = audio.TryGetVolumePercent(def.Id);
    Console.WriteLine($"設定: {(ok ? "成功" : "失敗")};讀回: {(v is null ? "無法讀取" : $"{v:0}%")}");
}
