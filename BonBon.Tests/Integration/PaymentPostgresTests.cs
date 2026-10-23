using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.Tests.Payments;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BonBon.Tests.Integration;

[Collection("PaymentPostgres")]
public sealed class PaymentPostgresTests(PaymentPostgresFixture database,ITestOutputHelper output)
{
    [PaymentPostgresFact]
    public async Task Migration_schema_and_rollback_are_verified()
    {
        Assert.True(database.MigrationRollbackVerified);
        Assert.True(database.SourceQuoteGuardWas002);
        Assert.Equal(1,await database.ScalarAsync<int>("SELECT version FROM bonbon.payment_gateway_schema"));
        Assert.Equal(9L,await database.ScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='payment'
              AND column_name IN ('idempotency_key','checkout_url','expires_at','provider_transaction_no','response_code',
                'transaction_status','updated_at','needs_reconciliation','reconciliation_reason')
            """));
        Assert.Equal(4L,await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='bonbon' AND c.relname LIKE 'uq_vnpay_sandbox_%' AND i.indisunique AND i.indisvalid AND i.indisready AND i.indpred IS NOT NULL
            """));
        Assert.True(await database.ScalarAsync<bool>("""
            SELECT NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid IN ('bonbon.payment'::regclass,'bonbon.payment_notification'::regclass) AND NOT convalidated)
                AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='bonbon.payment_notification'::regclass AND contype='f' AND confrelid='bonbon.payment'::regclass)
                AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='bonbon.payment_notification'::regclass AND contype='u')
            """));
        output.WriteLine($"Database={PaymentPostgresFixture.Database}, restricted role={PaymentPostgresFixture.Role}; baseline hash={database.BaselineHash}; migration rollback verified; no DROP.");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private static PaymentService Service(BonBon.DataAccess.BonBonDbContext context,Clock clock)=>new(
        new PaymentRepository(context),new VnPayGateway(Options.Create(PaymentTestData.GatewayOptions)),
        Options.Create(new PaymentOptions { Enabled=true }),clock);
    private async Task<(PaymentPostgresFixture.Seed Seed,Clock Clock)> ApprovedAsync()
    {
        var seed=await database.SeedAsync();
        await using var context=await database.ContextAsync();
        Assert.True((await new BookingService(new BookingRepository(context)).ApproveAsync(seed.Owner,seed.First)).IsSuccess);
        return (seed,new Clock());
    }
    private async Task<BonBon.Business.Common.ServiceResult<PaymentCheckoutResponse>> CreateAsync(PaymentPostgresFixture.Seed seed,Clock clock,string key="stable_checkout_request_0001",Guid? customer=null)
    {
        await using var context=await database.ContextAsync();
        return await Service(context,clock).CreateAsync(customer??seed.Customer,new CreatePaymentRequest(seed.First),key,"127.0.0.1");
    }
    private static Dictionary<string,string> Callback(PaymentCheckoutResponse payment,Clock clock,string code="00",string state="00",string? transaction=null)
    {
        var values=new Dictionary<string,string> {
            ["vnp_TmnCode"]="TEST0001",["vnp_TxnRef"]=payment.PaymentId.ToString("N"),["vnp_Amount"]=(payment.Amount*100).ToString("0",System.Globalization.CultureInfo.InvariantCulture),
            ["vnp_ResponseCode"]=code,["vnp_TransactionStatus"]=state,["vnp_TransactionNo"]=transaction??Random.Shared.NextInt64(1,999999999999999).ToString(),
            ["vnp_PayDate"]=clock.Now.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMddHHmmss") };
        Sign(values); return values;
    }
    private static void Sign(Dictionary<string,string> values)
    {
        var canonical=string.Join("&",values.Where(x=>x.Key!="vnp_SecureHash" && x.Value!="").OrderBy(x=>x.Key,StringComparer.Ordinal)
            .Select(x=>$"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));
        values["vnp_SecureHash"]=Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(PaymentTestData.TestSecret),Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    private async Task<string> IpnAsync(Dictionary<string,string> values,Clock clock)
    { await using var context=await database.ContextAsync(); return (await Service(context,clock).HandleIpnAsync(values)).RspCode; }
    private async Task<string> BookingStatusAsync(Guid id)=>await database.ScalarAsync<string>("SELECT status FROM bonbon.booking WHERE booking_id=@id",("id",id));
    private async Task<long> AuditsAsync(Guid id)=>await database.ScalarAsync<long>("SELECT count(*) FROM bonbon.payment_notification WHERE payment_id=@id",("id",id));

    [PaymentPostgresFact]
    public async Task Booking_approval_rejection_ownership_and_replays_work()
    {
        var seed=await database.SeedAsync(); await using var context=await database.ContextAsync();
        var service=new BookingService(new BookingRepository(context));
        Assert.Equal("BOOKING_NOT_OWNER",(await service.ApproveAsync(seed.Outsider,seed.First)).ErrorCode);
        Assert.Equal("BOOKING_NOT_FOUND",(await service.ApproveAsync(seed.Owner,Guid.NewGuid())).ErrorCode);
        Assert.True((await service.ApproveAsync(seed.Owner,seed.First)).IsSuccess);
        Assert.True((await service.ApproveAsync(seed.Owner,seed.First)).IsSuccess);
        Assert.True((await service.RejectAsync(seed.Owner,seed.Second)).IsSuccess);
        Assert.True((await service.RejectAsync(seed.Owner,seed.Second)).IsSuccess);
        Assert.Equal("BOOKING_STATE_CONFLICT",(await service.ApproveAsync(seed.Owner,seed.Second)).ErrorCode);
        Assert.Equal(1300000m,await database.ScalarAsync<decimal>("SELECT quoted_rental_amount FROM bonbon.booking WHERE booking_id=@id",("id",seed.First)));
    }

    [PaymentPostgresFact]
    public async Task Two_overlapping_booking_approvals_serialize_on_test_vehicle()
    {
        var seed=await database.SeedAsync();
        await using var first=await database.ContextAsync(); await using var second=await database.ContextAsync();
        var results=await Task.WhenAll(new BookingService(new BookingRepository(first)).ApproveAsync(seed.Owner,seed.First),
            new BookingService(new BookingRepository(second)).ApproveAsync(seed.Owner,seed.Second));
        Assert.Single(results,x=>x.IsSuccess); Assert.Single(results,x=>x.ErrorCode=="SCHEDULE_CONFLICT");
    }

    [PaymentPostgresFact]
    public async Task Booking_approval_rechecks_owner_customer_roles_and_vehicle_state()
    {
        foreach(var scenario in new[]{"owner","customer","vehicle"})
        {
            var seed=await database.SeedAsync();
            if(scenario=="vehicle") await database.ExecuteAsync("UPDATE bonbon.vehicle SET status='Suspended' WHERE vehicle_id=@id",("id",seed.Vehicle));
            else await database.ExecuteAsync("UPDATE bonbon.account_role SET status='Suspended' WHERE account_id=@id",("id",scenario=="owner"?seed.Owner:seed.Customer));
            await using var context=await database.ContextAsync();
            var result=await new BookingService(new BookingRepository(context)).ApproveAsync(seed.Owner,seed.First);
            Assert.Equal(scenario switch {"owner"=>"OWNER_INACTIVE","customer"=>"CUSTOMER_INACTIVE",_=>"VEHICLE_UNAVAILABLE"},result.ErrorCode);
            Assert.Equal("PendingApproval",await BookingStatusAsync(seed.First));
        }
    }

    [PaymentPostgresFact]
    public async Task Existing_quote_create_booking_function_and_mine_work_after_migrations()
    {
        var seed=await database.SeedAsync();
        await using var context=await database.ContextAsync();
        var service=new BookingService(new BookingRepository(context));
        var start=seed.Start.AddDays(4); var end=start.AddDays(2);
        var quote=await service.QuoteAsync(new BonBon.Business.DTOs.Bookings.BookingQuoteRequest { VehicleId=seed.Vehicle,RentalType="Daily",StartAt=start,EndAt=end });
        Assert.True(quote.IsSuccess); Assert.Equal(1300000m,quote.Data!.QuotedRentalAmount);
        var created=await service.CreateAsync(seed.Customer,new BonBon.Business.DTOs.Bookings.CreateBookingRequest {VehicleId=seed.Vehicle,RentalType="Daily",StartAt=start,EndAt=end});
        Assert.True(created.IsSuccess,created.ErrorCode); var booking=created.Data!;
        await database.ExecuteAsync("INSERT INTO public.payment_test_manifest VALUES(@run,'booking',@id)",("run",database.RunId),("id",booking.BookingId));
        Assert.Equal("PendingApproval",booking.Status);
        Assert.True((await service.ApproveAsync(seed.Owner,booking.BookingId)).IsSuccess);
        var mine=await service.GetMineAsync(seed.Customer); Assert.True(mine.IsSuccess);
        Assert.Contains(mine.Data!,item=>item.BookingId==booking.BookingId && item.Status=="AwaitingPayment" && item.TotalAmount==1300000m);
    }

    [PaymentPostgresFact]
    public async Task Create_payment_reuses_key_and_server_amount_without_duplicate_attempt()
    {
        var (seed,clock)=await ApprovedAsync(); var first=await CreateAsync(seed,clock); var replay=await CreateAsync(seed,clock);
        Assert.True(first.IsSuccess); Assert.True(replay.IsSuccess); Assert.Equal(first.Data!.PaymentId,replay.Data!.PaymentId); Assert.True(replay.Data.IsReplay);
        Assert.Equal(1300000m,first.Data.Amount);
        await using(var context=await database.ContextAsync())
        {
            Assert.Equal("PAYMENT_NOT_FOUND",(await Service(context,clock).GetAsync(seed.Outsider,first.Data.PaymentId)).ErrorCode);
            Assert.Equal(first.Data.PaymentId,(await Service(context,clock).GetAsync(seed.Customer,first.Data.PaymentId)).Data!.PaymentId);
        }
        Assert.Equal("PAYMENT_IN_PROGRESS",(await CreateAsync(seed,clock,"different_checkout_request_0001")).ErrorCode);
        Assert.Equal(1L,await database.ScalarAsync<long>("SELECT count(*) FROM bonbon.payment WHERE booking_id=@id",("id",seed.First)));
    }

    [PaymentPostgresFact]
    public async Task Two_concurrent_creates_with_same_key_return_one_attempt()
    {
        var (seed,clock)=await ApprovedAsync();
        var results=await Task.WhenAll(CreateAsync(seed,clock),CreateAsync(seed,clock));
        Assert.All(results,r=>Assert.True(r.IsSuccess,r.ErrorCode));
        Assert.Equal(results[0].Data!.PaymentId,results[1].Data!.PaymentId);
        Assert.Single(results,r=>!r.Data!.IsReplay);
    }

    [PaymentPostgresFact]
    public async Task Two_concurrent_creates_with_different_keys_have_only_one_winner()
    {
        var (seed,clock)=await ApprovedAsync();
        var results=await Task.WhenAll(CreateAsync(seed,clock,"concurrent_checkout_key_0001"),CreateAsync(seed,clock,"concurrent_checkout_key_0002"));
        Assert.Single(results,r=>r.IsSuccess); Assert.Single(results,r=>r.ErrorCode=="PAYMENT_IN_PROGRESS");
    }

    [PaymentPostgresFact]
    public async Task Pending_booking_other_customer_and_inactive_accounts_cannot_pay()
    {
        var seed=await database.SeedAsync(); var clock=new Clock();
        Assert.Equal("BOOKING_NOT_PAYABLE",(await CreateAsync(seed,clock)).ErrorCode);
        await using(var ctx=await database.ContextAsync()) Assert.True((await new BookingService(new BookingRepository(ctx)).ApproveAsync(seed.Owner,seed.First)).IsSuccess);
        Assert.Equal("PAYMENT_FORBIDDEN",(await CreateAsync(seed,clock,customer:seed.Outsider)).ErrorCode);
        await database.ExecuteAsync("UPDATE bonbon.account_role SET status='Suspended' WHERE account_id=@id AND role_id=2",("id",seed.Customer));
        Assert.Equal("PAYMENT_ACTOR_INACTIVE",(await CreateAsync(seed,clock)).ErrorCode);
    }

    [PaymentPostgresFact]
    public async Task Success_ipn_commits_booking_payment_audit_and_duplicate_is_harmless()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!; var callback=Callback(payment,clock);
        Assert.Equal("00",await IpnAsync(callback,clock)); Assert.Equal("Confirmed",await BookingStatusAsync(seed.First));
        Assert.Equal("02",await IpnAsync(callback,clock)); Assert.Equal(1L,await AuditsAsync(payment.PaymentId));
        Assert.Equal("Succeeded",await database.ScalarAsync<string>("SELECT status FROM bonbon.payment WHERE payment_id=@id",("id",payment.PaymentId)));
        Assert.Equal(1300000m,await database.ScalarAsync<decimal>("SELECT quoted_rental_amount FROM bonbon.booking WHERE booking_id=@id",("id",seed.First)));
    }

    [PaymentPostgresFact]
    public async Task Two_concurrent_identical_ipns_confirm_once()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!; var callback=Callback(payment,clock);
        var results=await Task.WhenAll(IpnAsync(callback,clock),IpnAsync(callback,clock));
        Assert.Contains("00",results); Assert.Contains("02",results); Assert.Equal(1L,await AuditsAsync(payment.PaymentId));
    }

    [PaymentPostgresFact]
    public async Task Failure_cancel_and_success_after_terminal_result_require_reconciliation()
    {
        foreach(var code in new[]{"51","24"})
        {
            var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!;
            Assert.Equal("00",await IpnAsync(Callback(payment,clock,code,"02"),clock));
            Assert.Equal(code=="24"?"Cancelled":"Failed",await database.ScalarAsync<string>("SELECT status FROM bonbon.payment WHERE payment_id=@id",("id",payment.PaymentId)));
            Assert.Equal("00",await IpnAsync(Callback(payment,clock),clock));
            Assert.Equal("AwaitingPayment",await BookingStatusAsync(seed.First));
            Assert.True(await database.ScalarAsync<bool>("SELECT needs_reconciliation FROM bonbon.payment WHERE payment_id=@id",("id",payment.PaymentId)));
        }
    }

    [PaymentPostgresFact]
    public async Task Invalid_signature_reference_amount_and_return_do_not_write()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!;
        var invalid=Callback(payment,clock); invalid["vnp_SecureHash"]=new string('0',128); Assert.Equal("97",await IpnAsync(invalid,clock));
        invalid=Callback(payment,clock); invalid["vnp_TxnRef"]=Guid.NewGuid().ToString("N"); Sign(invalid); Assert.Equal("01",await IpnAsync(invalid,clock));
        invalid=Callback(payment,clock); invalid["vnp_Amount"]="100"; Sign(invalid); Assert.Equal("04",await IpnAsync(invalid,clock));
        invalid=Callback(payment,clock); invalid["vnp_TransactionNo"]="invalid"; Sign(invalid); Assert.Equal("97",await IpnAsync(invalid,clock));
        await using(var context=await database.ContextAsync()) Assert.True((await Service(context,clock).HandleReturnAsync(Callback(payment,clock))).IsSuccess);
        Assert.Equal(0L,await AuditsAsync(payment.PaymentId)); Assert.Equal("AwaitingPayment",await BookingStatusAsync(seed.First));
    }

    [PaymentPostgresFact]
    public async Task Late_success_is_audited_without_confirming_booking()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!; var callback=Callback(payment,clock);
        clock.Now=payment.ExpiresAt.AddSeconds(1);
        Assert.Equal("00",await IpnAsync(callback,clock)); Assert.Equal("AwaitingPayment",await BookingStatusAsync(seed.First));
        Assert.Equal("LATE_PAYMENT",await database.ScalarAsync<string>("SELECT reconciliation_reason FROM bonbon.payment WHERE payment_id=@id",("id",payment.PaymentId)));
    }

    [PaymentPostgresFact]
    public async Task Uncertain_or_expired_payment_blocks_new_checkout()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!;
        Assert.Equal("00",await IpnAsync(Callback(payment,clock,"07","02"),clock));
        Assert.Equal("PAYMENT_RECONCILIATION_REQUIRED",(await CreateAsync(seed,clock,"next_checkout_request_0001")).ErrorCode);
        var (other,otherClock)=await ApprovedAsync(); var expiring=(await CreateAsync(other,otherClock)).Data!;
        otherClock.Now=expiring.ExpiresAt.AddSeconds(1);
        Assert.Equal("PAYMENT_RECONCILIATION_REQUIRED",(await CreateAsync(other,otherClock)).ErrorCode);
    }

    [PaymentPostgresFact]
    public async Task Failure_after_success_does_not_downgrade_and_extra_receipt_is_retained()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!;
        Assert.Equal("00",await IpnAsync(Callback(payment,clock),clock));
        Assert.Equal("02",await IpnAsync(Callback(payment,clock,"24","02"),clock));
        Assert.Equal("Confirmed",await BookingStatusAsync(seed.First));
        Assert.Equal("00",await IpnAsync(Callback(payment,clock),clock));
        Assert.Equal(3L,await AuditsAsync(payment.PaymentId));
        Assert.Equal("MULTIPLE_PROVIDER_TRANSACTIONS",await database.ScalarAsync<string>("SELECT reconciliation_reason FROM bonbon.payment WHERE payment_id=@id",("id",payment.PaymentId)));
    }

    [PaymentPostgresFact]
    public async Task Same_provider_transaction_cannot_confirm_two_bookings()
    {
        var (first,c1)=await ApprovedAsync(); var p1=(await CreateAsync(first,c1)).Data!;
        var (second,c2)=await ApprovedAsync(); var p2=(await CreateAsync(second,c2)).Data!;
        var transaction=Random.Shared.NextInt64(1,999999999999999).ToString();
        Assert.Equal("00",await IpnAsync(Callback(p1,c1,transaction:transaction),c1));
        Assert.Equal("00",await IpnAsync(Callback(p2,c2,transaction:transaction),c2));
        Assert.Equal("AwaitingPayment",await BookingStatusAsync(second.First));
        Assert.Equal("PROVIDER_TRANSACTION_REUSED",await database.ScalarAsync<string>("SELECT reconciliation_reason FROM bonbon.payment WHERE payment_id=@id",("id",p2.PaymentId)));
    }

    [PaymentPostgresFact]
    public async Task Statement_failure_after_payment_update_rolls_back_payment_booking_and_audit()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!; var callback=Callback(payment,clock);
        // Tiny test-DB-only trigger: raises immediately for exactly one synthetic UUID.
        await database.ExecuteAsync($"""
            CREATE FUNCTION bonbon.payment_test_abort_confirm() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN IF NEW.booking_id='{seed.First:D}'::uuid AND NEW.status='Confirmed' THEN RAISE EXCEPTION 'Synthetic transaction rollback' USING ERRCODE='23514'; END IF; RETURN NEW; END $f$;
            CREATE TRIGGER payment_test_abort_confirm BEFORE UPDATE ON bonbon.booking FOR EACH ROW EXECUTE FUNCTION bonbon.payment_test_abort_confirm();
            """
            );
        try
        {
            Assert.Equal("99",await IpnAsync(callback,clock)); Assert.Equal("AwaitingPayment",await BookingStatusAsync(seed.First));
            Assert.Equal("Pending",await database.ScalarAsync<string>("SELECT status FROM bonbon.payment WHERE payment_id=@id",("id",payment.PaymentId)));
            Assert.Equal(0L,await AuditsAsync(payment.PaymentId));
        }
        finally { await database.ExecuteAsync("DROP TRIGGER payment_test_abort_confirm ON bonbon.booking; DROP FUNCTION bonbon.payment_test_abort_confirm();"); }
        Assert.Equal("00",await IpnAsync(callback,clock)); Assert.Equal("Confirmed",await BookingStatusAsync(seed.First));
    }

    [PaymentPostgresFact]
    public async Task Quote_guard_005_protects_participants_price_dates_and_decision_transition()
    {
        var seed=await database.SeedAsync();
        var alternate=await database.SeedAsync();
        foreach(var change in new[]{"customer_account_id=@other","vehicle_id=@vehicle","unit_price_snapshot=650001,status='AwaitingPayment'"})
        {
            var ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("UPDATE bonbon.booking SET "+change+" WHERE booking_id=@id",("other",seed.Outsider),("vehicle",alternate.Vehicle),("id",seed.First)));
            Assert.Equal("23514",ex.SqlState);
        }
        await using(var ctx=await database.ContextAsync()) Assert.True((await new BookingService(new BookingRepository(ctx)).RejectAsync(seed.Owner,seed.First)).IsSuccess);
        foreach(var change in new[]{"unit_price_snapshot=650001","status='PendingApproval'","start_at=start_at+interval '30 minutes',end_at=end_at+interval '30 minutes'"})
        { var ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("UPDATE bonbon.booking SET "+change+" WHERE booking_id=@id",("id",seed.First))); Assert.Equal("23514",ex.SqlState); }
    }

    [PaymentPostgresFact]
    public async Task Real_unique_indexes_and_check_constraints_reject_conflicting_rows()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!;
        var ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("""
            INSERT INTO bonbon.payment(booking_id,provider_reference,provider,amount,currency,status,created_at,expires_at,checkout_url,idempotency_key,updated_at)
            SELECT booking_id,@ref,provider,amount,currency,status,created_at,expires_at,checkout_url,'another_checkout_request_0001',updated_at FROM bonbon.payment WHERE payment_id=@id
            """,("ref",Guid.NewGuid().ToString("N")),("id",payment.PaymentId)));
        Assert.Equal("23505",ex.SqlState); Assert.Equal("uq_vnpay_sandbox_active",ex.ConstraintName);
        ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("UPDATE bonbon.payment SET expires_at=created_at+interval '31 minutes' WHERE payment_id=@id",("id",payment.PaymentId)));
        Assert.Equal("23514",ex.SqlState);
        ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("UPDATE bonbon.payment SET amount=10000000000 WHERE payment_id=@id",("id",payment.PaymentId)));
        Assert.Equal("23514",ex.SqlState);
    }

    [PaymentPostgresFact]
    public async Task Migration_rerun_fails_without_changing_committed_schema()
    {
        await using var c=await database.OpenAsync();
        await using var cmd=new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Sql","004_vnpay_sandbox.sql")),c);
        var ex=await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync()); Assert.Equal("P0001",ex.SqlState);
        await using var rollback=new NpgsqlCommand("ROLLBACK",c); await rollback.ExecuteNonQueryAsync();
        Assert.Equal(1,await database.ScalarAsync<int>("SELECT version FROM bonbon.payment_gateway_schema"));
    }

    [PaymentPostgresFact]
    public async Task Index_keys_predicates_column_types_and_check_definitions_match_contract()
    {
        await using var connection=await database.OpenAsync();
        await using var command=new NpgsqlCommand("""
            SELECT c.relname,pg_get_indexdef(i.indexrelid),pg_get_expr(i.indpred,i.indrelid)
            FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='bonbon' AND c.relname LIKE 'uq_vnpay_sandbox_%' ORDER BY c.relname
            """,connection);
        await using(var reader=await command.ExecuteReaderAsync())
        {
            var count=0;
            while(await reader.ReadAsync())
            {
                count++; var name=reader.GetString(0); var definition=reader.GetString(1); var predicate=reader.GetString(2);
                Assert.Contains("ON bonbon.payment USING btree",definition); Assert.Contains("VNPAY_SANDBOX",predicate);
                Assert.Contains(name switch { "uq_vnpay_sandbox_idempotency"=>"(booking_id, idempotency_key)","uq_vnpay_sandbox_transaction"=>"(provider_transaction_no)",_=>"(booking_id)" },definition);
                if(name.EndsWith("active")) { Assert.Contains("Pending",predicate); Assert.Contains("Processing",predicate); Assert.DoesNotContain("Succeeded",predicate); }
                if(name.EndsWith("accepted_success")) { Assert.Contains("Succeeded",predicate); Assert.Contains("NOT needs_reconciliation",predicate); }
                if(name.EndsWith("transaction")) Assert.Contains("provider_transaction_no IS NOT NULL",predicate);
                output.WriteLine(name+": "+predicate);
            }
            Assert.Equal(4,count);
        }
        Assert.True(await database.ScalarAsync<bool>("""
            SELECT count(*)=9 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='payment' AND (
              (column_name='idempotency_key' AND data_type='character varying' AND character_maximum_length=100)
              OR (column_name='checkout_url' AND data_type='text')
              OR (column_name IN ('expires_at','updated_at') AND data_type='timestamp with time zone')
              OR (column_name='provider_transaction_no' AND character_maximum_length=15)
              OR (column_name IN ('response_code','transaction_status') AND character_maximum_length=2)
              OR (column_name='needs_reconciliation' AND data_type='boolean' AND is_nullable='NO' AND column_default='false')
              OR (column_name='reconciliation_reason' AND character_maximum_length=100))
            """));
        Assert.Contains("9999999999",await database.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='bonbon.payment'::regclass AND conname='ck_vnpay_sandbox_attempt'"));
    }

    [PaymentPostgresFact]
    public async Task Idempotency_accepted_success_and_provider_transaction_unique_indexes_are_enforced()
    {
        var (seed,clock)=await ApprovedAsync(); var payment=(await CreateAsync(seed,clock)).Data!;
        Assert.Equal("00",await IpnAsync(Callback(payment,clock,"51","02"),clock));
        var ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("""
            INSERT INTO bonbon.payment(booking_id,provider_reference,provider,amount,currency,status,created_at,expires_at,checkout_url,idempotency_key,updated_at)
            SELECT booking_id,@ref,provider,amount,currency,status,created_at,expires_at,checkout_url,idempotency_key,updated_at FROM bonbon.payment WHERE payment_id=@id
            """,("ref",Guid.NewGuid().ToString("N")),("id",payment.PaymentId)));
        Assert.Equal("23505",ex.SqlState); Assert.Equal("uq_vnpay_sandbox_idempotency",ex.ConstraintName);
        var (paidSeed,paidClock)=await ApprovedAsync(); var paid=(await CreateAsync(paidSeed,paidClock)).Data!;
        var callback=Callback(paid,paidClock); Assert.Equal("00",await IpnAsync(callback,paidClock));
        ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("""
            INSERT INTO bonbon.payment(booking_id,provider_reference,provider,amount,currency,status,created_at,expires_at,checkout_url,idempotency_key,updated_at,paid_at,provider_transaction_no)
            SELECT booking_id,@ref,provider,amount,currency,status,created_at,expires_at,checkout_url,'separate_checkout_key_0001',updated_at,paid_at,@txn FROM bonbon.payment WHERE payment_id=@id
            """,("ref",Guid.NewGuid().ToString("N")),("txn",Random.Shared.NextInt64(1,999999999999999).ToString()),("id",paid.PaymentId)));
        Assert.Equal("23505",ex.SqlState); Assert.Equal("uq_vnpay_sandbox_accepted_success",ex.ConstraintName);
        ex=await Assert.ThrowsAsync<PostgresException>(()=>database.ExecuteAsync("UPDATE bonbon.payment SET provider_transaction_no=@txn WHERE payment_id=@id",("txn",callback["vnp_TransactionNo"]),("id",payment.PaymentId)));
        Assert.Equal("23505",ex.SqlState); Assert.Equal("uq_vnpay_sandbox_transaction",ex.ConstraintName);
    }

    [PaymentPostgresFact]
    public async Task Retained_test_data_is_identifiable_and_wallet_is_untouched()
    {
        Assert.False(await database.ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM bonbon.account a WHERE NOT EXISTS(SELECT 1 FROM public.payment_test_manifest m WHERE m.kind='account' AND m.record_id=a.account_id))
              OR EXISTS(SELECT 1 FROM bonbon.vehicle v WHERE NOT EXISTS(SELECT 1 FROM public.payment_test_manifest m WHERE m.kind='vehicle' AND m.record_id=v.vehicle_id))
              OR EXISTS(SELECT 1 FROM bonbon.booking b WHERE NOT EXISTS(SELECT 1 FROM public.payment_test_manifest m WHERE m.kind='booking' AND m.record_id=b.booking_id))
            """));
        Assert.Equal(0L,await database.ScalarAsync<long>("SELECT count(*) FROM bonbon.wallet"));
        Assert.Equal(0L,await database.ScalarAsync<long>("SELECT count(*) FROM bonbon.wallet_transaction"));
        output.WriteLine("Retained, no cleanup/delete: "+await database.ScalarAsync<string>("""
            SELECT 'accounts='||(SELECT count(*) FROM bonbon.account)||', vehicles='||(SELECT count(*) FROM bonbon.vehicle)
                ||', bookings='||(SELECT count(*) FROM bonbon.booking)||', payments='||(SELECT count(*) FROM bonbon.payment)
                ||', notifications='||(SELECT count(*) FROM bonbon.payment_notification)||', manifest='||(SELECT count(*) FROM public.payment_test_manifest)
            """));
        output.WriteLine("Test run="+database.RunId+"; IDs are recorded in public.payment_test_manifest, linked Payment/audit remain in test DB only.");
    }

    [Fact(Skip="Requires an independent PostgreSQL server: server crash, disk/network faults and sustained load are forbidden on the team server.")]
    public void Server_fault_recovery_and_load_tests_require_independent_server() { }
}
