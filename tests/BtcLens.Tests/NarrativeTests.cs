using System.Net;
using System.Text.Json;
using BtcLens.Core;
using BtcLens.Infrastructure;

namespace BtcLens.Tests;
public sealed class NarrativeTests
{
    private static StudySnapshot Study(){var market=MarketReadingTests.Fixture();return StudyWorkflow.Build(market,MarketReader.Build(market,[]),RuleProfile.Default,StudyIntent.Empty);}
    [Fact] public async Task QianwenConnectionAndSchemaOutputUseBearerWithoutCredentialsInEvidence()
    {
        var handler=new RecordingHandler();var client=new QianwenClient(new(handler){BaseAddress=new(QianwenClient.BaseUrl)});
        await client.VerifyAsync("test-secret","qwen3.8-max",default);var study=Study();var result=await client.ExplainAsync("test-secret","qwen3.8-max",study,default);
        Assert.Equal(study.Id,result.SnapshotId);Assert.Equal(80,result.OutputTokens);Assert.Contains(handler.Bodies,b=>b.Contains("json_schema"));
        Assert.All(handler.Bodies,b=>{Assert.DoesNotContain("test-secret",b);Assert.DoesNotContain("api_key",b);});Assert.All(handler.Auth,a=>Assert.Equal("Bearer test-secret",a));
    }
    [Theory][InlineData("deepseek-v4-pro-0813")][InlineData("qwen3.5-omni-plus")]
    public async Task OtherUserSelectedModelsUseJsonObjectAndAppropriateTokenField(string model)
    {
        var handler=new RecordingHandler();var client=new QianwenClient(new(handler){BaseAddress=new(QianwenClient.BaseUrl)});
        await client.ExplainAsync("test-secret",model,Study(),default);Assert.Contains("json_object",handler.Bodies.Single());
        Assert.Contains(model=="qwen3.5-omni-plus"?"max_tokens":"max_completion_tokens",handler.Bodies.Single());
    }
    [Fact] public void CitationOrSnapshotMismatchCannotEnterValidatedNarrative()
    {
        var study=Study();var result=new ModelNarrative("解释",[new("支持",["invented-id"])],[],[],"test",study.Id,study.KnownAt,1,1);
        Assert.Throws<InvalidDataException>(()=>NarrativeRules.Validate(result,study));Assert.Throws<InvalidDataException>(()=>NarrativeRules.Validate(result with {SnapshotId="other"},study));
        NarrativeRules.Validate(result with {Support=[new("说明",["flow-window","rule:L34-target"])]},study);
    }
    [Fact] public async Task AuthenticationFailureDoesNotEchoKeyOrRawServerResponse()
    {
        var handler=new RecordingHandler{Fail=true};var client=new QianwenClient(new(handler){BaseAddress=new(QianwenClient.BaseUrl)});
        var ex=await Assert.ThrowsAsync<InvalidDataException>(()=>client.VerifyAsync("test-secret","qwen3.8-max",default));Assert.DoesNotContain("test-secret",ex.Message);
    }
    private sealed class RecordingHandler:HttpMessageHandler
    {
        public bool Fail;public List<string> Bodies=[];public List<string> Auth=[];
        private readonly OfflineNarrativeHandler inner=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));Auth.Add(request.Headers.Authorization!.ToString());
            if(Fail)return new(HttpStatusCode.Unauthorized){Content=new StringContent("test-secret")};
            using var doc=JsonDocument.Parse(Bodies.Last());var verify=doc.RootElement.GetProperty("messages").GetArrayLength()==1;
            var content=verify?"{\"ok\":true}":"{\"summary\":\"解释快照\",\"support\":[{\"text\":\"窗口证据\",\"evidenceIds\":[\"flow-window\"]}],\"conflicts\":[],\"watch\":[]}";
            return new(HttpStatusCode.OK){Content=new StringContent(JsonCodec.Write(new {model=doc.RootElement.GetProperty("model").GetString(),choices=new[]{new {finish_reason="stop",message=new {content}}},usage=new {prompt_tokens=100,completion_tokens=80}}))};
        }
    }
}
