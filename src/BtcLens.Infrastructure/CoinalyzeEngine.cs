using BtcLens.Application;
using BtcLens.Core;

namespace BtcLens.Infrastructure;

public sealed class CoinalyzeEngine
{
    private readonly CoinalyzeClient client;
    private readonly ILensStore store;
    private readonly Func<string?> key;
    private readonly SemaphoreSlim gate=new(1,1);
    private ProviderFlowView view;
    public event Action? Changed;
    public ProviderFlowView View => Volatile.Read(ref view);
    public CoinalyzeEngine(CoinalyzeClient client,ILensStore store,Func<string?> key)
    {
        this.client=client;this.store=store;this.key=key;
        try
        {
            view=JsonCodec.Read<ProviderFlowView>(store.GetSetting("coinalyze:snapshot")??"") with {Configured=!string.IsNullOrWhiteSpace(key())};
            if(store.GetSetting("coinalyze:selected") is {Length:>0} selected && !view.Selected.Order().SequenceEqual(JsonCodec.Read<string[]>(selected).Order()))
                view=view with {Status="市场选择已改变，等待刷新",FetchedAt=0,Selected=JsonCodec.Read<string[]>(selected),Volumes=[],OpenInterest=[]};
        }
        catch{view=ProviderFlowView.Empty with {Configured=!string.IsNullOrWhiteSpace(key())};}
    }
    public async Task<ProviderMarket[]> VerifyAsync(string candidate,CancellationToken ct) => await client.Markets(candidate,ct);
    public void Configure(ProviderMarket[] markets,string[] selected)
    {
        var chosen=markets.Where(m=>selected.Contains(m.Symbol)).ToArray();CoinalyzeClient.ValidateSelection(chosen);
        if(chosen.Length!=selected.Length)throw new ArgumentException("所选市场已变化，请刷新市场列表。");
        store.SetSetting("coinalyze:selected",JsonCodec.Write(selected));
        var next=new ProviderFlowView(true,"等待历史数据刷新",null,0,markets,selected,[],[]);
        store.SetSetting("coinalyze:snapshot",JsonCodec.Write(next));view=next;Changed?.Invoke();
    }
    public void Disconnect(){view=ProviderFlowView.Empty;store.SetSetting("coinalyze:snapshot","");Changed?.Invoke();}
    public async Task RefreshAsync(CancellationToken ct)
    {
        if(!await gate.WaitAsync(0,ct))return;
        var requestedKey=key();var selectedSetting=store.GetSetting("coinalyze:selected");
        try
        {
            if(string.IsNullOrWhiteSpace(requestedKey)){if(view.Configured)Disconnect();return;}
            var legacy=view.Available.Length>0 && view.Available.Any(m=>m.ExchangeCode is null);
            var previousDefault=legacy && view.Selected.Order().SequenceEqual(CoinalyzeClient.DefaultSelection(view.Available).Order());
            var markets=view.Available.Length>0 && !legacy?view.Available:await client.Markets(requestedKey,ct);
            if(previousDefault){selectedSetting=JsonCodec.Write(CoinalyzeClient.DefaultSelection(markets));store.SetSetting("coinalyze:selected",selectedSetting);}
            var selected=string.IsNullOrWhiteSpace(selectedSetting)?CoinalyzeClient.DefaultSelection(markets):JsonCodec.Read<string[]>(selectedSetting);
            var chosen=markets.Where(m=>selected.Contains(m.Symbol)).ToArray();
            if(chosen.Length!=selected.Length)throw new InvalidDataException("所选 Coinalyze 市场不再可用，请重新选择。");
            var end=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/60_000*60_000;
            var history=await client.History(requestedKey,chosen,end,ct);
            if(requestedKey!=key() || selectedSetting!=store.GetSetting("coinalyze:selected"))return;
            var next=new ProviderFlowView(true,"Coinalyze 历史买卖量 / OI · 90 秒刷新",null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),markets,selected,history.Volumes,history.Oi);
            store.SetSetting("coinalyze:snapshot",JsonCodec.Write(next));view=next;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        {
            if(requestedKey==key() && selectedSetting==store.GetSetting("coinalyze:selected"))
                view=view with {Configured=!string.IsNullOrWhiteSpace(requestedKey),Status="Coinalyze 数据暂不可用",Error=ex is InvalidDataException or ArgumentException?ex.Message:"Coinalyze 网络或数据格式异常；保留上次结果，暂停订单流确认。"};
        }
        finally{gate.Release();Changed?.Invoke();}
    }
}
