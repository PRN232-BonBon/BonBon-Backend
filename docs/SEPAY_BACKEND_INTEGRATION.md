# BonBon — SePay Payment Gateway Sandbox (Backend)

Phạm vi: provider `SEPAY_SANDBOX`; thanh toán toàn bộ tiền thuê từ `quoted_rental_amount`.
Không triển khai Frontend, Wallet, chia tiền chủ xe hoặc Refund. VNPay và dữ liệu cũ được giữ lại.
SePay mặc định tắt; không dùng credential hay endpoint production.

## Tài liệu chính thức đã đối chiếu

- [Sandbox](https://developer.sepay.vn/vi/cong-thanh-toan/sandbox).
- [Bắt đầu](https://developer.sepay.vn/vi/cong-thanh-toan/bat-dau).
- [Checkout form và chữ ký](https://developer.sepay.vn/vi/cong-thanh-toan/API/don-hang/form-thanh-toan).
- [IPN và hai loại key riêng](https://developer.sepay.vn/vi/cong-thanh-toan/IPN).
- [REST / Basic Auth](https://developer.sepay.vn/vi/cong-thanh-toan/API/tong-quan).
- [Chi tiết đơn theo invoice](https://developer.sepay.vn/vi/cong-thanh-toan/API/don-hang/chi-tiet-don-hang).

Lưu ý: kết quả tìm kiếm cũ có thể ghi `{order_id}`. Nội dung tài liệu hiện tại ghi
`GET /v1/order/detail/{order_invoice_number}`; code dùng invoice đã lưu trong database.
Không dùng SePay Bank Hub/UserAPI giao dịch ngân hàng thay cho Payment Gateway API.

## Kiến trúc và kiểm soát

`PaymentController → SePayCheckoutService → PaymentService → PaymentRepository(SEPAY_SANDBOX)`.
Phần dùng chung giữ nguyên kiểm tra Customer sở hữu Booking, tài khoản/role Active, Vehicle Available,
AwaitingPayment, thời gian nhận xe, giá snapshot, Payment đã thành công và idempotency.
Khoá PostgreSQL theo Vehicle → Booking → Payment; không gọi gateway khi giữ transaction/row lock.

Checkout chỉ ký dữ liệu cục bộ. DB giữ Pending, invoice UUID dạng 32 ký tự, expiry, idempotency key
và hash ràng buộc MerchantId trong `checkout_url` (`sepay:form:<sha256>`).
Đây là marker nội bộ, KHÔNG phải checkout URL có thể điều hướng. Đổi MerchantId giữa giao dịch
sẽ bị từ chối và cần xử lý giao dịch cũ trước.

`SePaySignatureService`: thứ tự cố định theo tài liệu, chuỗi `name=value` nối bằng dấu phẩy,
bỏ trường rỗng/`0`, HMAC-SHA256 trên UTF-8 và Base64 của bytes. `lang` không thuộc chuỗi ký.
Không sắp xếp alphabet và không URL-encode trước khi ký. Có vector đối chiếu Node.js độc lập.

`SePayController → SePayIpnHandler → SePayReconciliationService → SePayGateway.QueryAsync`:
IPN xác thực `X-Secret-Key` bằng digest comparison constant-time; IPN key riêng với merchant key.
Chỉ nhận ORDER_PAID/TRANSACTION_VOID, hạn chế body 32 KiB. Notification timestamp tương lai quá
5 phút bị từ chối. Thông báo cũ phải được query lại thay vì tin timestamp/payload.

REST chỉ GET tới sandbox cố định bằng Basic Auth `MerchantId:MerchantSecretKey`, không redirect,
proxy, HTTP body/auth logging hoặc retry tạo đơn. Timeout query 12 giây, body tối đa 256 KiB.
Response REST được xác thực trong scope merchant; nếu có trường merchant thì phải khớp thêm.
Đối chiếu invoice, order UUID/order_id, VND nguyên, số tiền và transaction UUID/type/status/date.
Thông tin thẻ và khách hàng ngoài các trường cần thiết không được lưu audit.

Chỉ order CAPTURED + transaction PAYMENT/APPROVED đã được REST xác minh mới tạo bằng chứng thành công.
Fingerprint ổn định theo invoice/order/transaction/status giúp IPN gửi lại hoặc admin query không ghi receipt trùng.
Payment + Booking Confirmed + notification + bằng chứng REST được ghi cùng một transaction.
Callback đến muộn, thay đổi trạng thái Booking, account inactive, giao dịch thứ hai hoặc VOID cần đối soát;
giữ receipt cũ, không tự undo Booking hoặc cho phép thu tiền lại.

## API contract cuối cùng

| Endpoint | Xác thực | Hành vi |
|---|---|---|
| POST `/api/payments` | JWT Customer + một `Idempotency-Key` | Body `{ "bookingId": "<uuid>", "provider": "SEPAY_SANDBOX" }`; tạo hoặc trả cùng attempt |
| GET `/api/payments/{paymentId}?provider=SEPAY_SANDBOX` | JWT Customer sở hữu Booking, role Active | Trạng thái thực tế từ DB; không gọi gateway |
| POST `/api/payments/sepay/ipn` | Không JWT; `X-Secret-Key` riêng | Xác thực IPN + REST query + transaction DB |
| GET `/api/payments/sepay/return?paymentId=<uuid>` | Public | 302 tới FrontendResultUrl cố định, kèm provider/paymentId; không ghi trạng thái |
| POST `/api/payments/sepay/{paymentId}/reconcile` | JWT Admin + account/role Admin Active trong DB | Chỉ query invoice của Payment hiện có, không tạo checkout |

Không truyền provider vẫn giữ contract VNPay cũ. Không có endpoint public để tạo callback/simulator SePay.
Customer không được gọi reconcile. SePay không được bật ngoài môi trường cô lập PaymentLocal/PaymentStaging.

Ví dụ checkout response HTTP 200 (giá minh họa; ID/signature không phải credential thật):

```json
{
  "paymentId": "<uuid>",
  "bookingId": "<uuid>",
  "provider": "SEPAY_SANDBOX",
  "amount": 1300000,
  "currency": "VND",
  "status": "Pending",
  "expiresAt": "<ISO-8601>",
  "isReplay": false,
  "checkout": {
    "action": "https://pay-sandbox.sepay.vn/v1/checkout/init",
    "method": "POST",
    "fields": [
      { "name": "order_amount", "value": "1300000" },
      { "name": "merchant", "value": "<merchant-id>" },
      { "name": "currency", "value": "VND" },
      { "name": "operation", "value": "PURCHASE" },
      { "name": "order_description", "value": "<server-generated>" },
      { "name": "order_invoice_number", "value": "<32-hex-invoice>" },
      { "name": "success_url", "value": "<configured-return>?paymentId=<uuid>" },
      { "name": "error_url", "value": "<same-return>" },
      { "name": "cancel_url", "value": "<same-return>" },
      { "name": "lang", "value": "vi" },
      { "name": "signature", "value": "<base64-hmac>" }
    ]
  }
}
```

Frontend sau này phải giữ thứ tự array khi tạo hidden input, submit form POST tới đúng action allowlisted,
không coi đây là checkoutUrl JSON. Không lưu/log form đã ký hoặc truyền merchant/IPN secret cho trình duyệt.
`checkout` có thể null khi attempt không còn đủ điều kiện. Dùng cùng Idempotency-Key cho retry mạng;
không tự sinh key/attempt mới khi chưa rõ kết quả. Gửi lại form cùng invoice có thể bị SePay từ chối invoice trùng;
chưa được xác minh E2E, không coi việc replay form là quyền tạo thêm đơn bên provider.

GET response giữ shape PaymentStatusResponse hiện tại:
`paymentId, bookingId, amount, currency, status, expiresAt, requiresReconciliation`.
Trạng thái Pending/Processing/Succeeded/Failed/Cancelled; flag đối soát độc lập.
Chỉ hiển thị đã thanh toán khi GET trả Succeeded **và** requiresReconciliation=false.
Trang Return không được tự công nhận thành công từ query hoặc HTTP 302.

HTTP lỗi checkout/GET: 400 request/provider sai; 401 JWT thiếu/hết hạn; 403 sai chủ Booking/actor inactive;
404 không tìm thấy; 409 Booking không thể thanh toán/đã có attempt/cần đối soát;
503 disabled/configuration/schema/database chưa sẵn sàng.
IPN: 200 đã áp dụng/trùng/đã ghi nhận cần đối soát; 400 dữ liệu không khớp; 401 secret sai;
404 invoice không tồn tại; 409 bằng chứng transaction chưa đủ; 503 query/DB/config chưa sẵn sàng.
200 IPN chỉ xác nhận đã xử lý thông báo, không đồng nghĩa Booking được Confirmed.

## Cấu hình private

File local đã tạo (không đưa vào Git/Docker context):
`BonBon.API/.payment-sepay.local.json`.
Mẫu public không có secret: `deployment/sepay-sandbox.settings.example.json`.
File chỉ có root `SePay`; không được override Auth/JWT/connection string từ file này.
Script `tools/Start-PaymentLocal.ps1` đặt `BONBON_SEPAY_CONFIG` tới đường dẫn tuyệt đối của file đó.
Trong PaymentStaging quản trị viên đặt đường dẫn private tương ứng; biến môi trường `SePay__...` có ưu tiên sau file.
PaymentStaging profile cũ dùng một HTTPS origin chung cho API/Frontend. `SePay:PublicOrigin` phải trùng
`PaymentStaging:PublicOrigin`, FrontendResultUrl phải là cùng origin + `/payment/result`.
Chỉ bật sau khi quản trị viên xác nhận riêng `PaymentStaging:SePayMerchantVerified`,
`PaymentStaging:SePayCallbacksVerified`, `PaymentStaging:SePayReconciliationPlanVerified`.
Các flag readiness VNPay cũ không thay thế được xác nhận SePay. Startup còn đọc marker/index bằng runtime role.
Profile mới `PaymentStaging:DatabaseProfile=SePayDedicated` dùng database/cluster riêng và cho phép
Frontend HTTPS origin khác Backend, được pin tại `PaymentStaging:FrontendOrigin`; xem
[SEPAY_STAGING_SETUP.md](SEPAY_STAGING_SETUP.md). Origin Frontend hiện để trống, SePay vẫn tắt.

| Key | Giá trị/nguồn |
|---|---|
| Enabled | false mặc định; chỉ bật sau khi kiểm tra đầy đủ môi trường cô lập |
| MerchantId | Sandbox Merchant ID do SePay cấp |
| MerchantSecretKey | Test-mode key `spsk_test_...`, dùng ký checkout và REST Basic Auth |
| IpnSecretKey | Key riêng >=24 ký tự, đặt tại Dashboard IPN Auth Type SECRET_KEY; khác merchant key |
| SandboxCheckoutUrl | `https://pay-sandbox.sepay.vn/v1/checkout/init` |
| SandboxApiBaseUrl | `https://pgapi-sandbox.sepay.vn` |
| PublicOrigin | HTTPS origin riêng của Backend sandbox, không path/query; không domain production |
| ReturnUrl | Cùng origin trên + `/api/payments/sepay/return` |
| FrontendResultUrl | HTTPS Frontend sandbox + `/payment/result`, không query/fragment |

Không gửi key vào chat, URL hay log. MerchantId được gửi trong form theo contract nhưng vẫn không in trong báo cáo.
Không dùng key live. Đăng nhập SePay, kích hoạt Cổng thanh toán, chọn Sandbox/Test mode;
lưu Merchant ID và key một lần vào secret local. Tại Cổng thanh toán → Cấu hình → IPN,
khai báo `PublicOrigin + /api/payments/sepay/ipn`, Auth Type SECRET_KEY và key riêng giống IpnSecretKey.

Lúc triển khai ban đầu chưa có credential/HTTPS, nên giữ Enabled=false. Cập nhật kiểm tra ngày 16/11/2026:
MerchantId/MerchantSecretKey trong secret local đã SET và xác thực được qua REST Sandbox thật.
IpnSecretKey, PublicOrigin, ReturnUrl và FrontendResultUrl vẫn MISSING; Enabled vẫn false.
Việc xác thực REST không chứng minh IPN hoặc checkout đã hoạt động end-to-end.

## Schema và migration

Proposal `database/proposals/007_sepay_sandbox.sql` dựa trên baseline local đã review:
004 VNPay + 005 quote guard + 006 provider/audit MoMo đã tồn tại trước yêu cầu SePay.
Không chạy 001/002/003 tuần tự máy móc và không áp dụng proposal lên database dùng chung.
Nếu môi trường mới chưa có 006, DBA phải review prerequisite hoặc tách phần provider/audit tương đương;
không bỏ qua guard trong 007. Không triển khai/tự bật MoMo do prerequisite schema.

007 thêm marker SePay, 4 unique indexes, CHECK provider riêng, UUID transaction, bảng evidence và rejected audit.
Mở rộng 2 unique indexes cross-provider để chặn nhiều attempt/accepted receipt giữa các provider.
Giữ nguyên CHECK và index VNPay/MoMo; widening varchar có CHECK theo provider, không nới rule legacy.
Evidence có FK tới cặp payment/fingerprint trong payment_notification; không xóa audit qua cascade.
`response_code`/`transaction_status` trong Payment/audit là mã nội bộ chuẩn hóa (`00/00` hoặc `99/99`),
không phải mã nguyên gốc SePay; trạng thái SePay và order/transaction identity nằm trong evidence.
Rejected audit chỉ giữ reason code và hash, deduplicate để tránh lưu payload bí mật/PII hoặc phình dữ liệu do retry.
Timestamp rejected là lần gặp đầu tiên, không phải nhật ký đầy đủ mỗi HTTP delivery.

Đã áp dụng **chỉ** lên portable PostgreSQL `127.0.0.1:55432/bonbon_payment_local`:
target/marker/role được xác minh, backup pg_dump custom và archive inventory hợp lệ;
injected failure trước COMMIT rollback đúng; checksum tất cả Payment/Booking/audit cũ không đổi;
sau đó migration và grants tối thiểu cho role local thành công. Chưa diễn tập restore toàn bộ archive.
Script local `tools/Initialize-SePayLocal.ps1` từ chối target khác hoặc marker SePay đã tồn tại, không tự rerun.
Grants local: SELECT marker; SELECT/INSERT evidence và rejected audit. Runtime không cần DDL.
Migration không sửa function/trigger Booking. Sau khi có receipt mới, ưu tiên roll forward; không drop lịch sử tài chính.

SQL chỉ đọc để đối chiếu trên database test đã xác minh:

```sql
SELECT current_database(), current_user, inet_server_addr(), inet_server_port();
SELECT * FROM bonbon.sepay_gateway_schema;
SELECT p.payment_id,p.booking_id,p.provider,p.amount,p.currency,p.status,
       p.expires_at,p.provider_transaction_no,p.needs_reconciliation,p.reconciliation_reason,b.status AS booking_status
FROM bonbon.payment p JOIN bonbon.booking b USING(booking_id)
WHERE p.payment_id = '<payment-id>'::uuid AND p.provider='SEPAY_SANDBOX';
SELECT fingerprint,disposition,reconciliation_reason,received_at
FROM bonbon.payment_notification WHERE payment_id='<payment-id>'::uuid;
SELECT fingerprint,evidence FROM bonbon.sepay_payment_evidence WHERE payment_id='<payment-id>'::uuid;
SELECT reason,count(*) FROM bonbon.sepay_rejected_notification GROUP BY reason;
```

Không SELECT toàn bộ checkout_url của VNPay cũ để đưa vào log/chat vì có chữ ký và merchant reference.

## Kiểm thử / Swagger / Postman

1. Backend local: `powershell -File tools/Start-PaymentLocal.ps1`; listener loopback 5288/5287.
2. GET `/health`. Swagger chỉ có trong môi trường ứng dụng cho phép; có thể dùng Postman không cần Swagger.
3. Login synthetic Customer bằng tài khoản trong file ignored `.payment-local-accounts.local.json`.
   Token chỉ lưu trong Postman local variable/private environment, không export collection có token.
4. Customer tạo Booking mới trong tương lai, Owner synthetic duyệt. Không dùng Booking/receipt cũ đã Confirmed.
5. POST `/api/payments` JWT + stable Idempotency-Key + provider SEPAY_SANDBOX.
   Khi disabled phải 503, không có attempt mới; không sửa flag để vượt guard.
6. Sau khi quản trị viên có HTTPS/merchant thật và bật riêng sandbox: nhận ordered form fields;
   Frontend mới cần tích hợp form POST (chưa làm trong phạm vi Backend này).
7. Người dùng tự thao tác trên SePay Sandbox. Không dùng thẻ/tiền thật, không dùng simulator làm bằng chứng E2E.
8. IPN qua HTTPS public → REST query → atomic DB. Đối chiếu Payment, Booking, transaction UUID, audit/evidence và GET JWT.
9. Admin dùng endpoint reconcile khi cần; chỉ GET gateway, không gửi callback giả hoặc tạo lại Payment.
10. Test sai secret/amount/currency/invoice, duplicate, late, query timeout/401/429, reload và request đồng thời.

Chạy test độc lập: `dotnet restore`, `dotnet build BonBon.sln -c Release`,
`dotnet test BonBon.Tests/BonBon.Tests.csproj -c Release`.
Local PostgreSQL test là opt-in:
`BONBON_SEPAY_LOCAL_TEST=I_CONFIRM_LOCAL_SEPAY_ONLY` và
`BONBON_SEPAY_LOCAL_CONFIG=<absolute private PaymentLocal file>`.
Test tự xác minh loopback/database/marker/role trước ghi, dùng synthetic fixture riêng và FAKE SePay REST.
Có test trigger lỗi chỉ cho UUID Booking synthetic để chứng minh rollback Payment/Booking/audit/evidence;
trigger/function thử được dọn trong finally. Không dùng fixture DROP DATABASE/schema.
Dữ liệu synthetic được giữ để kiểm tra, không xóa dữ liệu cũ. Các nhóm database khác vẫn skipped nếu chưa opt-in.

## HTTPS và điều kiện E2E còn thiếu

Quản trị viên phải cung cấp hostname HTTPS sandbox riêng, TLS tin cậy, routing `/api/payments/sepay/ipn`
POST public không JWT/IP tester restriction; Return GET public. Các API tạo/GET/reconcile vẫn JWT/role.
Giới hạn body/rate tại proxy, tin forwarded headers chỉ từ proxy đã cấu hình; không expose PostgreSQL,
Swagger/debug/admin hoặc LOCAL IPN simulator. Không dùng domain/database production để thử.
Không có tunnel/proxy nào được mở bởi bước lập trình này.

Chưa có UI SePay; chưa kiểm thử redirect/browser form thực tế, merchant, TLS/IPN delivery hoặc response REST thực tế.
Ngày transaction trong tài liệu không có offset: hiện đọc theo UTC+07:00; cần xác minh timezone với SePay trước bật E2E.
Parser yêu cầu UUID transaction theo response hiện tại. Nếu merchant trả format khác, dừng và review contract,
không nới rule hoặc tự đoán reference để ghi receipt.

Chưa có worker đối soát định kỳ/backoff/rate-budget, Booking TTL giải phóng lịch, quy trình phê duyệt xử lý tiền đến muộn,
refund/void từ hệ thống, payout/Wallet hoặc production incident workflow.
Admin query có thể xác minh và xác nhận Pending còn hạn; giao dịch có flag đối soát/đã quá hạn vẫn giữ cần đối soát,
không tự bỏ flag dù provider báo đã thu tiền. VOID và CANCELLED thiếu chứng cứ terminal cũng giữ cần kiểm tra,
không tự cho phép thu lại. Checkout expiry nội bộ không bảo đảm provider ngừng nhận tiền.

Lỗi thường gặp: PAYMENT_DISABLED (flag tắt), PAYMENT_CONFIGURATION_INVALID (key/HTTPS không hợp lệ),
PAYMENT_SCHEMA_UNAVAILABLE (marker/index chưa đủ), PAYMENT_RECONCILIATION_REQUIRED (attempt cũ chưa rõ),
IPN 401 (key tab IPN khác merchant key), query 503 (merchant 401/403, rate limit, timeout hoặc DB).
Không tự retry tạo checkout khi gặp các lỗi chưa rõ kết quả.

## Kết quả kiểm tra triển khai local

- `dotnet restore`: thành công; `dotnet build BonBon.sln -c Release --no-restore`: thành công.
- Lượt cuối `dotnet test`: **320 passed, 0 failed, 49 skipped**. Các nhóm DB khác chưa opt-in vẫn skipped;
  SePay local PostgreSQL test đã chạy, không bị skipped. Không gọi SePay thật.
- Test unit: vector HMAC/Base64 độc lập, thứ tự form, config sandbox/HTTPS/key riêng,
  HTTP 401/403/404/429/500/timeout/JSON lỗi, IPN auth/type/timestamp, sai invoice/amount/currency/merchant/transaction,
  duplicate, policy late, Return chỉ điều hướng, quyền Booking và schema/connection không sẵn sàng.
- Test PostgreSQL local thực hiện 15 tình huống trong một test group: success+duplicate, sai IPN key,
  sai amount/currency/invoice, query timeout/401/429, VOID, late, tạo đồng thời, IPN đồng thời,
  unique constraint thực, lỗi giữa transaction và admin reconciliation. Booking Approve/Reject dùng service thật.
  Đây là DB thật + **FAKE SePay REST**, không phải SePay Sandbox E2E.
- Migration rollback, checksum toàn bộ 57 Payment / 63 Booking có trước SePay và audit legacy: không thay đổi.
  Không còn trigger fault-injection sau test. Fixture synthetic được giữ riêng để kiểm tra, không xóa dữ liệu cũ.
- Backend local `/health` healthy, database `bonbon_payment_local`, databaseVerified=true.
  Customer synthetic login thành công; SePay GET/IPN trả 503 khi Enabled=false như thiết kế.
- 146 file Frontend được so checksum với thời điểm bắt đầu SePay: **0 file thay đổi**.
- 212 file source/config/docs được đối chiếu với credential trong secret local: không tìm thấy giá trị khớp.
  Private config được Git/Docker ignore và không được copy vào API/test build output.
- Không commit/push/deploy, không mở browser/tunnel, không truy cập DB nhóm hoặc tạo checkout thật.

### Kiểm tra kết nối Sandbox thật — 16/11/2026

- Chỉ GET `https://pgapi-sandbox.sepay.vn/v1/order?per_page=1&page=1`, theo API danh sách đơn được tài liệu hỗ trợ.
  Basic Auth lấy trực tiếp từ secret local ignored; không in/lưu credential, response body hoặc dữ liệu đơn.
- Có credential: HTTP 200, response có `data` array hợp lệ. Negative control cùng endpoint không credential: HTTP 401.
  Kết luận: Merchant Sandbox xác thực được tại endpoint danh sách đơn ở thời điểm kiểm tra.
- Dùng TLS validation mặc định, không chấp nhận certificate tùy ý, tắt redirect và proxy cho probe,
  timeout 15 giây và giới hạn response buffer. Secret file checksum không thay đổi.
- Probe quản trị độc lập chỉ để xác thực REST; không sửa/bỏ qua `SePayOptions.IsValid()` hoặc readiness của checkout.
  Không gọi checkout/init, không query/apply reconciliation cho Payment hiện có, không gửi IPN.
- Chưa kiểm thử thực tế order-detail cho một giao dịch SePay chính thức, form checkout, Return hoặc IPN delivery.
  Không có URL HTTPS để xác minh routing, TLS callback hay cấu hình tab IPN trong Dashboard.
- Readiness riêng `SePayMerchantVerified`, `SePayCallbacksVerified`, `SePayReconciliationPlanVerified` chưa được đặt;
  kết quả REST không tự động bật các flag hoặc `Enabled`.
- Backend local healthy, databaseVerified=true. SQL chỉ đọc bằng restricted runtime role và
  `default_transaction_read_only=on`: SePay marker version 1, 4 unique indexes hợp lệ,
  43 Payment synthetic, 19 notification và 19 evidence — khớp kết quả sau lượt lập trình trước.
- Unit/regression chạy lại, loại namespace Integration: **311 passed, 0 failed, 1 skipped**.
  Không chạy các integration test ghi dữ liệu; không thay đổi Payment/Booking, source nghiệp vụ hoặc Frontend.
- Bước tiếp: nhập IPN key riêng >=24 ký tự vào secret local, cung cấp hostname HTTPS sandbox đã được duyệt,
  cấu hình Return/Frontend URL, đăng ký IPN tại Dashboard với Auth Type SECRET_KEY và kiểm tra các readiness guard.
  Chỉ sau khi đủ điều kiện mới bật sandbox có chủ đích và người dùng tự thực hiện giao dịch synthetic mới.

## File của phần SePay này

Tạo mới:

```text
BonBon.API/Controllers/SePayController.cs
BonBon.Business/Configuration/SePayOptions.cs
BonBon.Business/DTOs/Payments/SePayDtos.cs
BonBon.Business/Services/Interfaces/ISePayServices.cs
BonBon.Business/Services/Implementations/SePaySignatureService.cs
BonBon.Business/Services/Implementations/SePayGateway.cs
BonBon.Business/Services/Implementations/SePayCheckoutService.cs
BonBon.Business/Services/Implementations/SePayIpnHandler.cs
BonBon.Business/Services/Implementations/SePayReconciliationService.cs
BonBon.DataAccess/Repositories/Interfaces/ISePayPaymentRepository.cs
BonBon.DataAccess/Repositories/Implementations/SePayPaymentRepository.cs
BonBon.Tests/Payments/SePayTests.cs
BonBon.Tests/Integration/SePayLocalPostgresTests.cs
database/proposals/007_sepay_sandbox.sql
tools/Initialize-SePayLocal.ps1
deployment/sepay-sandbox.settings.example.json
docs/SEPAY_BACKEND_INTEGRATION.md
docs/sepay-sandbox.http
```

Sửa để tích hợp provider/guard, bảo toàn contract VNPay:

```text
BonBon.API/Program.cs
BonBon.API/Controllers/PaymentController.cs
BonBon.API/Configuration/PaymentStaging.cs
BonBon.Business/DTOs/Payments/PaymentRequests.cs
BonBon.DataAccess/Repositories/Implementations/PaymentRepository.cs
BonBon.DataAccess/Repositories/Interfaces/PaymentContracts.cs
BonBon.Tests/Payments/PaymentStagingSafetyTests.cs
tools/Start-PaymentLocal.ps1
```

Cập nhật cleanup: toàn bộ gateway/API/DI/test MoMo đã gỡ. Chỉ giữ proposal 006 và schema/index/audit tương thích vì đã áp dụng local và 007 còn phụ thuộc; không xóa hoặc đổi dữ liệu lịch sử. Các kết quả test phía trên là lịch sử trước cleanup; xem PAYMENT_CLEANUP_PUSH_REVIEW.md cho kết quả hiện tại.
Private ignored files và backup nằm ngoài danh sách source bàn giao; không stage/commit chúng.
