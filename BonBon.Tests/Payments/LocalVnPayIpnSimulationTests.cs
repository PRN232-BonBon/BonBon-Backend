using BonBon.API.Configuration;
using BonBon.Business.Services.Implementations;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace BonBon.Tests.Payments;

public class LocalVnPayIpnSimulationTests
{
    [Theory]
    [InlineData("Production",true,false)] [InlineData("Development",true,false)]
    [InlineData("PaymentStaging",true,false)] [InlineData("PaymentLocal",false,false)]
    [InlineData("PaymentLocal",true,true)]
    public void Only_explicit_desktop_environment_can_register(string environment,bool enabled,bool expected)
        => Assert.Equal(expected,LocalVnPayIpnSimulation.CanRegister(environment,enabled));

    [Theory]
    [InlineData("success","00",true)] [InlineData("failure","51",true)]
    [InlineData("cancel","24",true)] [InlineData("wrong-amount","00",true)]
    [InlineData("bad-signature","00",false)]
    public void Signed_payload_passes_real_gateway_except_intentionally_bad_signature(string scenario,string code,bool valid)
    {
        var query=LocalVnPayIpnSimulation.BuildQuery(PaymentTestData.GatewayOptions,"LOCALABC123",1300000m,scenario,"990123456789",PaymentTestData.Now);
        var fields=QueryHelpers.ParseQuery(query).ToDictionary(x=>x.Key,x=>x.Value.ToString());
        var result=new VnPayGateway(Options.Create(PaymentTestData.GatewayOptions)).Verify(fields);
        Assert.Equal(valid,result.IsValid);
        if(valid) {
            Assert.Equal(code,result.Notification!.ResponseCode);
            Assert.Equal(scenario=="wrong-amount"?130000100:130000000,result.Notification.AmountMinor);
        } else Assert.Equal("INVALID_SIGNATURE",result.ErrorCode);
    }
    [Fact] public void Unknown_scenario_cannot_sign()
        => Assert.Throws<ArgumentException>(()=>LocalVnPayIpnSimulation.BuildQuery(PaymentTestData.GatewayOptions,"LOCALABC123",1300000,"other","990123456789",PaymentTestData.Now));
}
