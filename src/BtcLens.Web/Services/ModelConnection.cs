using BtcLens.Application;
using BtcLens.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace BtcLens.Web.Services;

public sealed class ModelConnection(ILensStore store,IDataProtectionProvider protection,IConfiguration configuration)
{
    private readonly IDataProtector protector=protection.CreateProtector("BtcLens.Qianwen.ApiKey.v1");
    public string? Key {get {var external=configuration["Qianwen:ApiKey"];if(!string.IsNullOrWhiteSpace(external))return external;try{var saved=store.GetSetting("qianwen:key");return string.IsNullOrWhiteSpace(saved)?null:protector.Unprotect(saved);}catch(System.Security.Cryptography.CryptographicException){return null;}}}
    public string Model=>store.GetSetting("qianwen:model")??"qwen3.8-max";
    public bool EnvironmentManaged=>!string.IsNullOrWhiteSpace(configuration["Qianwen:ApiKey"]);
    public void Save(string key,string model){if(!QianwenClient.Models.Contains(model))throw new ArgumentException("模型不在已配置列表。");store.SetSetting("qianwen:key",protector.Protect(key));store.SetSetting("qianwen:model",model);}
    public void Clear()=>store.SetSetting("qianwen:key","");
}
