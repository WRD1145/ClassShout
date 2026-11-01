using CommunityToolkit.Mvvm.Input;

namespace ClassShout.Classroom.ViewModels;

public partial class ClassroomViewModel
{
    public bool IsHttpEngine => _speech.Engine == Core.Audio.SpeechEngine.Http;
    public string HttpTtsEndpoint { get => _speech.HttpSettings.Endpoint; set { _speech.HttpSettings.Endpoint = value; OnPropertyChanged(); } }
    public string HttpTtsVoice { get => _speech.HttpSettings.Voice; set { _speech.HttpSettings.Voice = value; OnPropertyChanged(); } }
    public string HttpTtsKey { get => _speech.HttpSettings.ApiKey; set { _speech.HttpSettings.ApiKey = value; OnPropertyChanged(); } }
    public bool HttpTtsFallback { get => _speech.HttpSettings.FallbackToSystem; set { _speech.HttpSettings.FallbackToSystem = value; OnPropertyChanged(); } }
    public bool HttpTtsAllowInsecure { get => _speech.HttpSettings.AllowInsecureHttp; set { _speech.HttpSettings.AllowInsecureHttp = value; OnPropertyChanged(); } }

    [RelayCommand]
    private void SaveHttpTts()
    {
        _speechSettings.Http = _speech.HttpSettings;
        AddLog("朗读", Core.Remote.LocalSettings.SaveSpeech(_speechSettings)
            ? "第三方语音配置已保存。" : "第三方语音配置保存失败，请检查目录权限。");
    }
}
