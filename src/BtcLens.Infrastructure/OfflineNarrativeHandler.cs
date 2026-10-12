using System.Net;
using System.Text;
using System.Text.Json;

namespace BtcLens.Infrastructure;

/// <summary>Registered only by Testing + OfflineFixture.</summary>
public sealed class OfflineNarrativeHandler:HttpMessageHandler
{
    public const string Key="offline-model-test-key";
    public int Requests {get;private set;}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Requests++;
        if(request.Headers.Authorization?.Parameter!=Key)return new(HttpStatusCode.Unauthorized){Content=new StringContent("Invalid test credential")};
        using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var body=json.RootElement;
        var messages=body.GetProperty("messages");var content=messages.GetArrayLength()==1?"{\"ok\":true}":
            JsonCodec.Write(new {summary="隔离测试：解释当前冻结快照，目标与订单流条件仍需核对。",
                support=new[]{new {text="图上背景和区域分别保留，参考候选不升级为完整 setup。",evidenceIds=new[]{"macro-context"}}},
                conflicts=new[]{new {text="目标资格及数据缺口需要继续核对。",evidenceIds=new[]{"target-state"}}},
                watch=new[]{new {text="观察同一窗口的现货、合约成交与 OI，再检查区域反应。",evidenceIds=new[]{"flow-window"}}}});
        return new(HttpStatusCode.OK){Content=new StringContent(JsonCodec.Write(new {model=body.GetProperty("model").GetString(),choices=new[]{new {message=new {content},finish_reason="stop"}},usage=new {prompt_tokens=100,completion_tokens=80}}),Encoding.UTF8,"application/json")};
    }
}
