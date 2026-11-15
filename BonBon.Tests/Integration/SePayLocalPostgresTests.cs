using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BonBon.API.Configuration;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.Tests.Integration;

public sealed class SePayLocalFactAttribute : FactAttribute {
    public SePayLocalFactAttribute() { if(Environment.GetEnvironmentVariable("BONBON_SEPAY_LOCAL_TEST")!="I_CONFIRM_LOCAL_SEPAY_ONLY") Skip="Opt-in isolated local database required; no connection by default."; }
}
public sealed class SePayLocalPostgresTests
{
    [SePayLocalFact]
    public async Task Real_PostgreSQL_checkout_IPN_and_reconciliation_with_FAKE_SePay_HTTP_only()
    {
        var path=Environment.GetEnvironmentVariable("BONBON_SEPAY_LOCAL_CONFIG")!;
        Assert.True(Path.IsPathFullyQualified(path));
        var config=PaymentStaging.AddLocalConfiguration(new ConfigurationBuilder(),path).Build();
        await using var source=PaymentLocal.CreateDataSource(config);await PaymentLocal.VerifySchemaAsync(source);
        // Separate non-pooled restricted connection leaves the two-connection app pool available for races.
        var cs=new NpgsqlConnectionStringBuilder(PaymentLocal.ValidateConfiguration(config)){Pooling=false};
        await using var c=new NpgsqlConnection(cs.ConnectionString);await c.OpenAsync();
        async Task Exec(string sql,params (string,object)[] values) {await using var cmd=new NpgsqlCommand(sql,c);foreach(var(k,v)in values)cmd.Parameters.AddWithValue(k,v);await cmd.ExecuteNonQueryAsync();}
        async Task<T> Scalar<T>(string sql,params (string,object)[] values) {await using var cmd=new NpgsqlCommand(sql,c);foreach(var(k,v)in values)cmd.Parameters.AddWithValue(k,v);return (T)(await cmd.ExecuteScalarAsync())!;}
        Assert.True(await Scalar<bool>("SELECT EXISTS(SELECT 1 FROM bonbon.sepay_gateway_schema WHERE version=1)"));
        var oldHash=await Scalar<string>("SELECT md5(COALESCE(jsonb_agg(to_jsonb(p) ORDER BY payment_id)::text,'')) FROM bonbon.payment p WHERE provider<>'SEPAY_SANDBOX'");
        var confirmedHash=await Scalar<string>("SELECT md5(COALESCE(jsonb_agg(to_jsonb(b) ORDER BY booking_id)::text,'')) FROM bonbon.booking b WHERE status='Confirmed'");
        var customer=Guid.NewGuid();var owner=Guid.NewGuid();var vehicle=Guid.NewGuid();var suffix=Guid.NewGuid().ToString("N");
        await Exec("""
            INSERT INTO bonbon.account(account_id,email,password_hash,full_name) VALUES(@customer,@ce,'synthetic-no-login','SePay integration synthetic'),(@owner,@oe,'synthetic-no-login','SePay synthetic owner');
            INSERT INTO bonbon.account_role(account_id,role_id,status) VALUES(@customer,2,'Active'),(@owner,3,'Active');
            INSERT INTO bonbon.vehicle(vehicle_id,owner_account_id,plate_number,brand,model,seats,transmission,pickup_address,hour_price,day_price,status,daily_mileage_limit_km,overage_fee_per_km,production_year,fuel_type,body_type)
              VALUES(@vehicle,@owner,@plate,'Local','SePay FAKE REST tests',4,'Automatic','Synthetic local only',100000,650000,'Available',400,3000,2025,'Gasoline','Sedan');
            """,("customer",customer),("owner",owner),("ce","sepay-"+suffix+"-customer@test.invalid"),("oe","sepay-"+suffix+"-owner@test.invalid"),("vehicle",vehicle),("plate","SEPAY-"+suffix[..20]));
        BonBonDbContext Context()=>new(new DbContextOptionsBuilder<BonBonDbContext>().UseNpgsql(source).Options);
        int index=0;var createdBookings=new List<Guid>();
        async Task<Guid> Booking(bool reject=false) {
            var start=DateTimeOffset.UtcNow.AddDays(160+index++*4);
            var id=await Scalar<Guid>("SELECT booking_id FROM bonbon.create_booking(@customer,@vehicle,'Daily',@start,@end)",("customer",customer),("vehicle",vehicle),("start",start),("end",start.AddDays(2)));
            await using var db=Context();var bookings=new BookingService(new BookingRepository(db));
            Assert.True((reject?await bookings.RejectAsync(owner,id):await bookings.ApproveAsync(owner,id)).IsSuccess);createdBookings.Add(id);return id;
        }
        var options=Options.Create(SePayTestData.Options);
        SePayCheckoutService Checkout(BonBonDbContext db,SePayGateway gateway)=>new(new SePayPaymentRepository(db),gateway,options,Options.Create(new PaymentOptions()),TimeProvider.System);
        async Task Verify(Guid payment,string ps,string bs,long audits,bool reconcile=false) {
            Assert.Equal(ps,await Scalar<string>("SELECT status FROM bonbon.payment WHERE payment_id=@id",("id",payment)));
            Assert.Equal(bs,await Scalar<string>("SELECT b.status FROM bonbon.booking b JOIN bonbon.payment p USING(booking_id) WHERE p.payment_id=@id",("id",payment)));
            Assert.Equal(audits,await Scalar<long>("SELECT count(*) FROM bonbon.payment_notification WHERE payment_id=@id",("id",payment)));
            Assert.Equal(audits,await Scalar<long>("SELECT count(*) FROM bonbon.sepay_payment_evidence WHERE payment_id=@id",("id",payment)));
            Assert.Equal(reconcile,await Scalar<bool>("SELECT needs_reconciliation FROM bonbon.payment WHERE payment_id=@id",("id",payment)));
            Assert.Equal(1300000m,await Scalar<decimal>("SELECT b.quoted_rental_amount FROM bonbon.booking b JOIN bonbon.payment p USING(booking_id) WHERE p.payment_id=@id",("id",payment)));
        }
        foreach(var scenario in new[]{"success-duplicate","bad-secret","bad-amount","bad-currency","bad-invoice","query-timeout","query-401","query-429","void","late","concurrent","concurrent-ipn","constraints","rollback","admin-query"}) {
            var booking=await Booking();SePayOrder? order=null;
            var handler=new SePayTestData.Handler(_=>scenario switch {
                "query-timeout"=>throw new TaskCanceledException(),"query-401"=>new(HttpStatusCode.Unauthorized),"query-429"=>new((HttpStatusCode)429),
                _=>new(HttpStatusCode.OK){Content=JsonContent.Create(new{data=order})}});
            using var gateway=new SePayGateway(new HttpClient(handler),options,new SePaySignatureService());
            await using var db=Context();var checkout=Checkout(db,gateway);var key="sepay_local_"+suffix;
            Guid payment;
            if(scenario=="concurrent") {
                async Task<BonBon.Business.Common.ServiceResult<SePayCheckoutResponse>> Race(){await using var racing=Context();return await Checkout(racing,gateway).CreateAsync(customer,new(booking,"SEPAY_SANDBOX"),key,"127.0.0.1",default);}
                var results=await Task.WhenAll(Race(),Race());Assert.All(results,r=>Assert.True(r.IsSuccess,r.ErrorCode));Assert.Equal(results[0].Data!.PaymentId,results[1].Data!.PaymentId);payment=results[0].Data!.PaymentId;
            } else {var created=await checkout.CreateAsync(customer,new(booking,"SEPAY_SANDBOX"),key,"127.0.0.1",default);Assert.True(created.IsSuccess,scenario+":"+created.ErrorCode);payment=created.Data!.PaymentId;Assert.NotNull(created.Data.Checkout);}
            Assert.Equal(0,handler.Calls); // Creating a checkout only signs form fields; it never calls SePay.
            var replay=await checkout.CreateAsync(customer,new(booking,"SEPAY_SANDBOX"),key,"127.0.0.1",default);Assert.Equal(payment,replay.Data!.PaymentId);Assert.True(replay.Data.IsReplay);
            Assert.Equal(1L,await Scalar<long>("SELECT count(*) FROM bonbon.payment WHERE booking_id=@id",("id",booking)));
            Assert.Equal("PAYMENT_FORBIDDEN",(await checkout.CreateAsync(owner,new(booking,"SEPAY_SANDBOX"),key,"127.0.0.1",default)).ErrorCode);
            Assert.Equal("PAYMENT_NOT_FOUND",(await checkout.GetAsync(owner,payment,default)).ErrorCode);
            var p=(await new SePayPaymentRepository(db).Payments.GetByReferenceAsync(payment.ToString("N")))!;
            if(scenario=="constraints") {
                await using var tx=await c.BeginTransactionAsync();
                await using(var savepoint=new NpgsqlCommand("SAVEPOINT sepay_unique_check",c,tx))await savepoint.ExecuteNonQueryAsync();
                await using var conflict=new NpgsqlCommand("INSERT INTO bonbon.payment(payment_id,booking_id,provider_reference,provider,amount,currency,status,created_at,expires_at,checkout_url,idempotency_key,updated_at) SELECT gen_random_uuid(),booking_id,replace(gen_random_uuid()::text,'-',''),provider,amount,currency,status,created_at,expires_at,checkout_url,idempotency_key,updated_at FROM bonbon.payment WHERE payment_id=@id",c,tx);conflict.Parameters.AddWithValue("id",payment);
                var ex=await Assert.ThrowsAsync<PostgresException>(()=>conflict.ExecuteNonQueryAsync());Assert.Equal("23505",ex.SqlState);
                await using(var rollback=new NpgsqlCommand("ROLLBACK TO SAVEPOINT sepay_unique_check",c,tx))await rollback.ExecuteNonQueryAsync();
                await tx.RollbackAsync();
                Assert.Equal(1L,await Scalar<long>("SELECT count(*) FROM bonbon.payment WHERE booking_id=@id",("id",booking)));
            }
            order=SePayTestData.Order(p,DateTimeOffset.UtcNow);var body=SePayTestData.Ipn(order,DateTimeOffset.UtcNow);
            if(scenario=="bad-amount")body=body with{Order=order with{Amount="1300001"}};
            if(scenario=="bad-currency")body=body with{Transaction=body.Transaction! with{Currency="USD"}};
            if(scenario=="bad-invoice")body=body with{Order=order with{Invoice=Guid.NewGuid().ToString("N")}};
            if(scenario=="void") {order=order with{Status="CANCELLED",Transactions=[order.Transactions![0] with{Status="VOIDED"}]};body=SePayTestData.Ipn(order,DateTimeOffset.UtcNow) with{NotificationType="TRANSACTION_VOID"};}
            TimeProvider clock=scenario=="late"?new SePayTestData.Clock(DateTimeOffset.UtcNow.AddHours(1)):TimeProvider.System;
            var repo=new SePayPaymentRepository(db);var recon=new SePayReconciliationService(repo,gateway,options,clock);var ipn=new SePayIpnHandler(options,gateway,recon,repo,clock);
            NpgsqlConnection? admin=null;string? fault=null;
            try {
                if(scenario=="rollback") {
                    using var secret=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!,".payment-local-db.local.json")));
                    var acs=new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=55432,Database="bonbon_payment_local",Username="postgres",Password=secret.RootElement.GetProperty("AdminPassword").GetString(),Pooling=false};
                    admin=new(acs.ConnectionString);await admin.OpenAsync();
                    await using(var guard=new NpgsqlCommand("SELECT current_database()='bonbon_payment_local' AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55432",admin))Assert.True((bool)(await guard.ExecuteScalarAsync())!);
                    fault="sepay_fault_"+Guid.NewGuid().ToString("N");
                    await using var install=new NpgsqlCommand($"CREATE FUNCTION bonbon.{fault}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Synthetic local atomicity test'; END $$; CREATE TRIGGER {fault} AFTER UPDATE ON bonbon.booking FOR EACH ROW WHEN(NEW.booking_id='{booking}') EXECUTE FUNCTION bonbon.{fault}()",admin);await install.ExecuteNonQueryAsync();
                }
                int http;
                if(scenario=="concurrent-ipn") {
                    async Task<int> RaceIpn(){await using var racing=Context();var rr=new SePayPaymentRepository(racing);var rs=new SePayReconciliationService(rr,gateway,options,TimeProvider.System);return await new SePayIpnHandler(options,gateway,rs,rr,TimeProvider.System).HandleAsync(options.Value.IpnSecretKey,body,default);}
                    var raced=await Task.WhenAll(RaceIpn(),RaceIpn());Assert.All(raced,s=>Assert.Equal(200,s));http=200;
                } else http=scenario=="admin-query"?await recon.ReconcileAsync(p.Reference,default):await ipn.HandleAsync(scenario=="bad-secret"?"wrong-key":options.Value.IpnSecretKey,body,default);
                if(scenario.StartsWith("bad-")) {Assert.Equal(scenario=="bad-secret"?401:scenario=="bad-invoice"?404:400,http);await Verify(payment,"Pending","AwaitingPayment",0);}
                else if(scenario is "query-timeout" or "query-401" or "query-429" or "rollback") {Assert.Equal(503,http);await Verify(payment,"Pending","AwaitingPayment",0);}
                else if(scenario=="void") {Assert.Equal(200,http);await Verify(payment,"Processing","AwaitingPayment",1,true);}
                else if(scenario=="late") {Assert.Equal(200,http);await Verify(payment,"Succeeded","AwaitingPayment",1,true);}
                else {Assert.Equal(200,http);await Verify(payment,"Succeeded","Confirmed",1);Assert.Equal(200,await ipn.HandleAsync(options.Value.IpnSecretKey,body,default));await Verify(payment,"Succeeded","Confirmed",1);}
                if(scenario=="query-timeout") {Assert.Equal(503,await ipn.HandleAsync(options.Value.IpnSecretKey,body,default));Assert.Equal(payment,(await checkout.CreateAsync(customer,new(booking,"SEPAY_SANDBOX"),key,"127.0.0.1",default)).Data!.PaymentId);Assert.Equal(1L,await Scalar<long>("SELECT count(*) FROM bonbon.payment WHERE booking_id=@id",("id",booking)));}
                if(scenario=="void")Assert.Equal("PAYMENT_RECONCILIATION_REQUIRED",(await checkout.CreateAsync(customer,new(booking,"SEPAY_SANDBOX"),key,"127.0.0.1",default)).ErrorCode);
            } finally {if(admin is not null){try{if(fault is not null){await using var drop=new NpgsqlCommand($"DROP TRIGGER IF EXISTS {fault} ON bonbon.booking; DROP FUNCTION IF EXISTS bonbon.{fault}()",admin);await drop.ExecuteNonQueryAsync();}}finally{await admin.DisposeAsync();}}}
        }
        var rejected=await Booking(true);await using(var rejectDb=Context())using(var gateway=new SePayGateway(new HttpClient(new SePayTestData.Handler(_=>throw new Exception("No HTTP"))),options,new SePaySignatureService()))
            Assert.Equal("BOOKING_NOT_PAYABLE",(await Checkout(rejectDb,gateway).CreateAsync(customer,new(rejected,"SEPAY_SANDBOX"),"sepay_rejected_"+suffix,"127.0.0.1",default)).ErrorCode);
        Assert.Equal(oldHash,await Scalar<string>("SELECT md5(COALESCE(jsonb_agg(to_jsonb(p) ORDER BY payment_id)::text,'')) FROM bonbon.payment p WHERE provider<>'SEPAY_SANDBOX'"));
        // Confirmed bookings which existed before this test are excluded from the new synthetic set.
        Assert.Equal(confirmedHash,await Scalar<string>("SELECT md5(COALESCE(jsonb_agg(to_jsonb(b) ORDER BY booking_id)::text,'')) FROM bonbon.booking b WHERE status='Confirmed' AND vehicle_id<>@vehicle",("vehicle",vehicle)));
    }
}
