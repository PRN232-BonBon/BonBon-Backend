# VNPay Sandbox thật trên trình duyệt — kế hoạch 15/11/2026

## Kết luận

Source có luồng checkout/IPN/Return/result và đã kiểm thử PostgreSQL local thành công,
sai amount/signature, duplicate, failure/cancel. Đây là LOCAL SIMULATION, không phải
bằng chứng VNPay nhận merchant, gọi được IPN hoặc đã xác nhận giao dịch thật.
Chưa có staging công khai được dựng/kiểm tra trong lượt này. Không mở tunnel, SSH,
deploy, bật production, migrate, tạo attempt hoặc gửi callback; không sửa đơn Confirmed.

Hai repository giữ review/payment-sandbox-web; thay đổi vẫn chưa stage/commit/push.
Các báo cáo local trước đây là bằng chứng lịch sử, không phải release staging đã deploy.

## Merchant local — chỉ trạng thái

| Cấu hình | Kết quả |
|---|---|
|VnPay__TmnCode|SET|
|VnPay__HashSecret|SET|
|VnPay__PaymentUrl|SET|
|VnPay__ReturnUrl cho public staging|INVALID|
|PaymentStaging__PublicOrigin|MISSING|
|PaymentStaging__IpnUrl|MISSING|
|PaymentReturn__FrontendResultUrl cho public staging|INVALID|

SET là có giá trị và qua kiểm tra định dạng; chưa xác minh credential/hostname bằng
giao dịch VNPay. Return/result hiện là localhost, hợp lệ với PaymentLocal nhưng không
phù hợp staging công khai. Không chép nguyên JSON local sang staging. Chỉ chuyển merchant
credentials qua secret manager/kênh quản trị an toàn; tạo DB/JWT riêng và hostname mới.
Các biến UPPER_CASE trong compose production là alias được compose map sang ASP.NET;
standalone compose mẫu sử dụng JSON private bind mount, không tự nạp VNPAY_* vào options.

## Cách ly simulator

- Backend chỉ đăng ký POST /api/local/vnpay-ipn-simulation trong PaymentLocal AND
  PaymentLocal:IpnSimulationEnabled=true. Production/Development/PaymentStaging không
  có route này. PaymentLocal khóa listener và DB vào loopback, không public hóa instance này.
- PaymentStaging mới còn từ chối startup nếu cờ simulator=true, kể cả Payment=false.
- Build Vite mặc định và build:payment-sandbox ép VITE_LOCAL_IPN_SIMULATION="false",
  không để .env local vô tình đưa nút giả lập lên web staging. Chỉ vite.payment-local
  bật cờ. Kiểm tra dist staging không còn cảnh báo/đường dẫn POST của simulator.
- Caddy mẫu chỉ allowlist API thật cần thiết và chặn /api/local/* bằng deny mặc định.
  .dockerignore loại tools, private JSON và bin/obj/cluster/cert/log local khỏi build context.
- Mã C# simulator vẫn có thể nằm trong assembly API chung; rào bảo vệ là không đăng ký
  route và fixed-loopback guard, không tuyên bố binary đã loại class. Không deploy image
  với environment PaymentLocal, không copy tools hoặc mở listener desktop qua tunnel.

## Phương án tận dụng hạ tầng hiện có

Khuyến nghị: admin chuẩn bị một instance/container API riêng + vhost HTTPS riêng và
static Frontend riêng trên máy staging/edge hiện có. Project bonbon-payment-sandbox
không extends/override compose production, không mount volume hoặc env file production.
Không dùng ba hostname frontend/API/admin production làm PublicOrigin. Không khởi chạy
Caddy thứ hai tranh80/443; admin review vhost vào edge đang quản lý hoặc dùng máy riêng.

Database staging: bonbon_payment_test, role bonbon_payment_test_runner trên PostgreSQL16.15
được DBA phê duyệt. Guard hiện pin port5432, DB/login/session_user/DB owner=test role,
NOSUPERUSER/NOCREATEDB/NOCREATEROLE, role connection limit4, pool2/min0, safe logging;
pin ExpectedDatabaseHost độc lập. Role/DB này không dùng cho production. Không suy rằng
DB test trên server nhóm đã có schema mới chỉ vì tên/marker đúng.

DBA đối chiếu schema baseline và code main: catalog004, tracking003, cấu trúc006/007/008,
Payment proposal004, quote guard proposal005 có discount/mileage/terms snapshots. Có hai
file004/005 khác mục đích; không chạy001/002/003 máy móc hoặc demo seeds. Cần backup/
recovery, migration/version review và phê duyệt target trước mọi SQL staging. Lượt này
không kết nối/chỉnh schema test của nhóm. Không sao chép dữ liệu/người dùng production.

Guard staging còn yêu cầu DB comment prefix bonbon-payment-test:v1:, bảng control stage
ready và marker/index/quote guard. Những marker này do DBA kiểm chứng/provision đúng,
không phải cờ để bỏ qua schema. Không đổi guard để lách khác biệt phiên bản/role.
Local portable bonbon_payment_local được giữ nguyên; không đưa nó lên Internet hoặc
tái dùng các receipt mô phỏng cho giao dịch thật.

## URL và cấu hình chính xác

Gọi O là origin HTTPS staging thực tế, cùng một origin cho UI và /api (không phải
chuỗi placeholder payment-sandbox.example.test trong template):

| Cấu hình ASP.NET | Giá trị |
|---|---|
|ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT|PaymentStaging|
|BONBON_PAYMENT_STAGING_CONFIG|Đường dẫn tuyệt đối private JSON, ví dụ /run/payment-staging.json trong container|
|ConnectionStrings__DefaultConnection|TestDB/role/host đã duyệt, pool2/min0, không log parameter/error detail|
|Jwt__Issuer / Audience|BonBon.PaymentStaging / BonBon.PaymentStaging.Web|
|Jwt__Key|Secret riêng >=64 ký tự, không copy JWT key production/local|
|VnPay__PaymentUrl|https://sandbox.vnpayment.vn/paymentv2/vpcpay.html|
|VnPay__TmnCode / VnPay__HashSecret|Merchant sandbox thật, chỉ private config|
|PaymentStaging__PublicOrigin|O|
|PaymentStaging__IpnUrl|O/api/payments/vnpay/ipn|
|VnPay__ReturnUrl|O/api/payments/vnpay/return/redirect|
|PaymentReturn__FrontendResultUrl|O/payment/result|
|AllowedHosts|Hostname staging;localhost;127.0.0.1, không wildcard|
|PaymentStaging__ExpectedDatabaseHost|Host testDB được pin độc lập|
|PaymentStaging__TrustedProxyAddresses|Chính xác IP proxy API nhìn thấy qua Docker bridge, không trust CIDR/wildcard|
|PaymentLocal__IpnSimulationEnabled|false|
|Payment__Enabled|false cho preflight; chỉ true sau toàn bộ gate và phê duyệt staging|

Ba readiness flags MerchantVerified/CallbacksVerified/ReconciliationPlanVerified phải
phản ánh kiểm tra thực tế, không đặttrue để vượt lỗi startup. Tạo config từ
deployment/payment-sandbox.settings.example.json; compose standalone và Caddy mẫu đã có
trong deployment. Backend không tự đăng ký IPN với VNPay khi điền PaymentStaging:IpnUrl.

Theo tài liệu PAY chính thức, IPN là GET qua SSL server-to-server, kiểm checksum trước
cập nhật và trả JSON RspCode/Message. Return là luồng trình duyệt, không thay thế IPN.
Gửi URL IPN đã dựng cho đầu mối VNPay để thiết lập, đồng thời xác nhận hostname/Return
được chấp nhận cho merchant đã kích hoạt. Không mặc định portal có quyền tự sửa hoặc
copy đường dẫn portal từ hướng dẫn bên thứ ba. Không gửi HashSecret qua chat/email.
Nguồn: https://sandbox.vnpayment.vn/apis/docs/thanh-toan-pay/pay.html

## Checklist phân công trước khi thanh toán

| Người phụ trách | Việc phải hoàn tất | Bằng chứng đạt |
|---|---|---|
|Admin hạ tầng|Hostname/DNS riêng, TLS chain tin cậy, instance/image/API/static artifact riêng|Không dùng route/volume/env production; không bypass TLS/SSH|
|DBA|DB/role/schema/marker/grants/backups đã review|Metadata đúng guard và code hiện tại; synthetic data only|
|Người quản lý merchant + VNPay|Credential sandbox hợp lệ, IPN/Return đăng ký đúng O|Xác nhận kết nối/hostname từ VNPay qua kênh quản trị|
|Admin proxy|Anonymous GET IPN/Return; human/API routes giới hạn tester và JWT|IPN không CAPTCHA/basic-auth/JWT/IP tester challenge; không HTML SPA thay JSON|
|Admin proxy|SPA /payment/result direct/refresh, CORS O, trusted forwarded headers|User IP/HTTPS nhận đúng; API5288/DB không mở Internet; simulator404|
|Nhóm ứng dụng|Build branch review thành image immutable và dist-payment-sandbox|Simulator UI không có; route giả lập404; production Payment vẫn tắt|
|Vận hành|Người phụ trách giao dịch treo/đến muộn, quy trình dừng/đối chiếu|Có người trực merchant sandbox; không ép trạng thái DB hoặc tự thu lại|
|Tester|Tài khoản Customer/Owner synthetic, xe Available và đơn mới|Không dùng lại Confirmed/mô phỏng/expired; pickup còn tương lai|

Không tự bật workflow production để lấy image review. Admin quản lý build image riêng
và kiểm tra quyền đọc private bind file của nonroot UID. JSON bind mount không tự remap
owner; không dùng chmod777. Không dump compose/env/inspect ra log chứa secret.
Các template chưa được validate/run bằng Docker/Caddy engine trên máy hiện tại.

Preflight do admin/tester thực hiện sau phê duyệt, không thực thi tại lượt review:

1. /health qua loopback xác nhận PaymentStaging/testDB/verified và Payment=false.
2. Frontend build: npm run build:payment-sandbox; phục vụ dist-payment-sandbox bằng edge.
3. UI login, catalog, mine/owner và /payment/result refresh đi đúng O/api.
4. GET IPN không có chữ ký phải không làm thay đổi DB; khi Payment=false nhận99,
   sau khi cấu hình đủ và bật staging nhận97. HTTP200 JSON, không redirect/challenge.
5. Return khi Payment=false phải302 tới O/payment/result?returnError=unavailable.
   Không dùng unsigned/signed giả để tuyên bố payment pass. Simulator phải404 trên edge
   và application staging; không được chỉ chặn edge nhưng vô tình bật ở ứng dụng.
6. Chốt merchant, callback và đối soát; admin bật Payment và3 flags chỉ instance staging
   trong change đã duyệt, recreate riêng container staging vì config không reload.

## Các bước bạn tự thực hiện một giao dịch sandbox

1. Đăng nhập Customer synthetic trên O, đặt xe ngày tương lai, lưu bookingId/quote.
2. Owner synthetic đăng nhập riêng, duyệt đúng đơn; kiểm AwaitingPayment và snapshot.
3. Customer mở /my-trips, bấm Thanh toán một lần. Ghi paymentId, expiresAt; key phải giữ
   nguyên khi retry/reload. Không copy signed checkout URL hoặc JWT vào tài liệu công khai.
4. Kiểm giá đúng, chỉ mở gateway sandbox. Bạn tự dùng phương tiện/OTP test do VNPay cấp;
   không dùng tiền/thẻ thật. Tôi không nhập/xác nhận giao dịch thay bạn.
5. Browser quay về Return Backend302 rồi O/payment/result?paymentId=...; đăng nhập lại
   nếu mất phiên. Trang chỉ GET Payment có JWT; query status=Succeeded/code00 không phải
   bằng chứng. IPN có thể đến sau Return: chờ/kiểm tra trạng thái, không thanh toán lại.
6. DBA/tester được cấp quyền đối chiếu metadata testDB, paymentId, provider_reference
   (=vnp_TxnRef), provider_transaction_no (=vnp_TransactionNo), amount/paidAt, audit hợp lệ
   và merchant sandbox. Không cần hiển thị TmnCode/HashSecret/signature/raw callback.
7. Chỉ PASS khi SQL Payment=Succeeded, Booking=Confirmed, amount đúng snapshot,
   requiresReconciliation=false; audit receipt thành công duy nhất, transaction reference
   khớp merchant; GET Payment và Frontend hiển thị cùng kết quả. Ghi là VNPay thật chỉ
   sau khi có IPN/merchant bằng chứng thật, không dùng dữ liệu LOCAL SIMULATION.

SQL đối chiếu (chỉ testDB, role được phép đọc, payment_id là biến psql đã ghi nhận):

```sql
BEGIN READ ONLY;
SET LOCAL statement_timeout='5s';
SET LOCAL lock_timeout='1s';
SELECT current_database(),current_user,inet_server_addr(),inet_server_port();
SELECT p.payment_id,p.provider_reference,p.provider_transaction_no,p.status AS payment_status,
       b.status AS booking_status,p.amount,b.quoted_rental_amount,p.expires_at,p.paid_at,
       p.needs_reconciliation,p.reconciliation_reason,p.response_code,p.transaction_status
FROM bonbon.payment p JOIN bonbon.booking b USING(booking_id)
WHERE p.payment_id=:'payment_id'::uuid;
SELECT response_code,transaction_status,disposition,amount_minor,gateway_paid_at,received_at
FROM bonbon.payment_notification WHERE payment_id=:'payment_id'::uuid;
ROLLBACK;
```

Đừng SELECT checkout_url/raw signed URL/secret khi lưu bằng chứng. API GET có thể tính
requiresReconciliation=true khi Pending/Processing hết hạn dù cột needs_reconciliation
vẫnfalse; phải đối chiếu expiresAt và thời điểm kiểm tra, không kết luận từ riêng cột DB.

## Hết hạn, retry, reload và các giới hạn cần chấp nhận

- Checkout mặc định15 phút, giới hạn thêm bởi giờ pickup. GET của Pending/Processing
  quá hạn trả cần đối soát; không tự đổi Failed hoặc xác nhận tiền.
- Frontend giữ key/receipt trong localStorage theo account+booking; reload/retry dùng
  cùng thao tác. Một pending attempt khác key bị409; không tự tạo key mới để giải lỗi.
- Khi đã nhớ paymentId, Frontend GET trước; terminal/expired/reconciliation không POST
  attempt mới. Mất storage/thiết bị không có recovery API; liên hệ người phụ trách,
  không clear storage hoặc dùng account khác để thu lại.
- Poll tối đa12 lần, cách5s cộng latency; manualcheck1 lần, lỗi dừng. IPN chậm sau cửa sổ
  poll cần bấm Kiểm tra lại; không cần tạo Payment mới.
- Return chỉ xác minh và điều hướng, không ghi trạng thái. Late success, reference/
  transaction bất thường hoặc không còn payable có thể Succeeded kèm reconciliation;
  Booking không tự Confirmed và Frontend không được báo thành công.
- QUERYDR, reconciliation worker, refund/reversal và Booking TTL chưa có. Checkout TTL
  không giải phóng lịch Booking. Một lượt happy-path sandbox có thể kiểm thử khi có
  người vận hành xử lý thất bại/treo, nhưng chưa đủ để tuyên bố sẵn sàng production.

## Kiểm tra local lượt này

Giữ nguyên gateway/service/repository. Bổ sung guard simulator và production host staging,
ép tắt simulator trong build web thường/staging, loại tools khỏi Docker build context.
Unit/API tests dùng mocks, integration opt-in tắt. Build Backend ra artifact path riêng
để không đụng DLL của API local đang chạy. Không restart app/DB, không gửi IPN mới.

Kết quả: Backend build0 errors (67 warnings hiện hữu),249 passed/0 failed/48 skipped.
Frontend typecheck và56 tests (31 Node+25 Vitest) đạt; build staging đạt. Kiểm tra JS
dist-payment-sandbox không có chuỗi endpoint/cảnh báo simulator. Vite còn cảnh báo
bundle lớn và annotation SignalR. JSON template và git diff --check đạt.
Build web thường cũng đạt và không có chuỗi simulator, kể cả khi cố tình truyền
VITE_LOCAL_IPN_SIMULATION=true từ môi trường lúc build. Quét các thay đổi không khớp
secret private local. Các tiến trình localhost vẫn hoạt động, publicIpnReady=false.

Source thay đổi của lượt này: Backend PaymentStaging.cs, PaymentStagingSafetyTests.cs,
.dockerignore, payment-sandbox.settings.example.json, tài liệu này và link runbook;
Frontend vite.config.ts, vite.payment-sandbox.config.ts. Không đổi nghiệp vụ Payment.
Chốt source/image được review trước khi admin dùng; hiện code vẫn chưa commit/push,
không thể mặc định image đang deploy đã chứa các chốt vừa bổ sung.
