using BtcLens.Application;
using Microsoft.AspNetCore.DataProtection;

namespace BtcLens.Web.Services;

public sealed class CoinalyzeConnection(ILensStore store,IDataProtectionProvider protection,IConfiguration configuration)
{
    private readonly IDataProtector protector=protection.CreateProtector("BtcLens.Coinalyze.ApiKey.v1");
    public string? Key
    {
        get
        {
            var configured=configuration["Coinalyze:ApiKey"];
            if(!string.IsNullOrWhiteSpace(configured))return configured;
            var encrypted=store.GetSetting("coinalyze:key");
            try{return string.IsNullOrWhiteSpace(encrypted)?null:protector.Unprotect(encrypted);}
            catch(System.Security.Cryptography.CryptographicException){return null;}
        }
    }
    public bool EnvironmentManaged=>!string.IsNullOrWhiteSpace(configuration["Coinalyze:ApiKey"]);
    public void Save(string candidate)=>store.SetSetting("coinalyze:key",protector.Protect(candidate));
    public void Clear()=>store.SetSetting("coinalyze:key","");
}
