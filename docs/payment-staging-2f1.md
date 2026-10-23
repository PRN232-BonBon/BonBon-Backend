# Giai đoạn 2F.1 — staging local trước VNPay sandbox E2E

## Trạng thái thực tế

Người dùng xác nhận chưa có merchant sandbox hoặc hostname HTTPS, yêu cầu chuẩn
bị local trước. Đã khởi động instance riêng và kiểm tra bằng HTTP/browser thật:

| Thành phần | Trạng thái |
| --- | --- |
| API | http://127.0.0.1:5288, environment PaymentStaging |
| Frontend | http://127.0.0.1:5289/my-trips, built assets riêng |
| Health API / qua proxy FE | healthy, databaseVerified=true, paymentEnabled=false |
| Database | bonbon_payment_test; role bonbon_payment_test_runner |
| Merchant sandbox | Thiếu TmnCode/HashSecret; không dùng credential demo |
| HTTPS/IPN/Return public | Chưa có domain/TLS, chưa mở tunnel hoặc deploy proxy |
| Payment | Tắt ở staging và cấu hình production mặc định |
| Giao dịch gateway | 0; không gọi VNPay, không dùng thẻ/tiền thật |

Đã kiểm tra TCP listener: cả 5288 và 5289 chỉ bind 127.0.0.1. PostgreSQL giữ kết
nối Tailscale hiện có, không mở port Internet. Website/domain/routing production
không thay đổi. Cả hai repository vẫn feature/payment, không commit/push/main.

## Cấu hình và chốt database

Không dùng môi trường Development hoặc Staging chung. Launcher ép cả
ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT=PaymentStaging, không đọc credential
bonbon_app của appsettings.Development.json. Bắt buộc chỉ rõ file secret tuyệt
đối bằng BONBON_PAYMENT_STAGING_CONFIG; không có fallback nếu thiếu/sai.

tools/Initialize-PaymentStaging.ps1 đọc credential **role test** đã có trong
.payment-test.local.json, sinh JWT key riêng và tạo .payment-staging.local.json.
Phiên bản initializer sau security review cần `-ExpectedDatabaseHost` từ pin đã được
quản trị viên phê duyệt độc lập; giá trị chỉ lưu trong file local, không suy ra từ credential.
Nếu config staging đã tồn tại thì giữ nguyên, không overwrite secret/flag.
Không in connection string, password, JWT key, HashSecret hoặc token. Các file
.payment-staging*.local.json được Git ignore và không copy vào test output.
JSON dotfile dùng PhysicalFileProvider explicit filename/ExclusionFilters.None
vì provider mặc định bỏ qua dot-prefixed file; config không reload khi đang chạy.

PaymentStaging.ValidateConfiguration từ chối trước khi mở connection nếu host,
port, database, username không đúng target cố định; không chấp nhận credential
bonbon_app. Từ chối multiplexing/NoResetOnClose, error/parameter logging, pool
không đúng giới hạn và JWT issuer/audience/key không dành riêng staging.

NpgsqlDataSource riêng có physical connection initializer, bảo vệ cả EF Auth
và repository gọi raw GetDbConnection(). Trước khi phục vụ connection, chỉ SELECT
identity/catalog: current_database, current_user/session_user, database owner,
marker database, PostgreSQL 16.15, role không superuser/CREATEDB/CREATEROLE,
connection limit 4, bảng marker/audit phải tồn tại. Startup/health kiểm tra
version 004, control stage=ready, quote guard đã nâng cấp và 4 unique index valid.
Không gọi Migrate/EnsureCreated/DDL. Schema vẫn tên **bonbon bên trong database
bonbon_payment_test**; đó không phải kết nối vào database chính bonbon.

Pool staging: maximum=2, minimum=0, idle lifetime=30s, pruning=10s, command timeout
10s, statement/lock timeout từ credential test. Tổng role limit=4 là giới hạn của
server, không nâng giới hạn. Không chạy nhiều API staging hoặc write integration
song song khi đang dùng staging; dừng API trước các suite cần nhiều connection.
Không thay đổi role/grants/server settings trong giai đoạn này.

Default production code path/config giữ nguyên; nhánh PaymentStaging mới được
chạy khi chọn đúng environment. Allowed CORS staging lấy đúng một origin riêng,
tránh việc configuration arrays hợp nhất thêm origin production. ForwardedHeaders
staging chỉ tin loopback proxy, forward limit 1. JWT dùng issuer
BonBon.PaymentStaging, audience BonBon.PaymentStaging.Web và key ngẫu nhiên riêng.
Logging HTTP/EF ở Warning, không bật request body/header/token/parameter logging.

Guard là kiểm tra chuẩn bị, không thay thế kiểm chứng toàn bộ schema/predicate
hoặc quyền vận hành sau này. Vẫn còn giới hạn schema guard runtime đã báo ở 2E.1.

## Dataset có thể đăng nhập

AuthService.Register tạo BCrypt hash, Account Active + Customer Active; Login
kiểm tra BCrypt/account và trả JWT với active roles. Smoke thực tế đã dùng hai
email synthetic dạng stage-<UUID>-customer/owner@test.invalid. Không copy dữ liệu
hoặc password từ người dùng thật. Password được sinh ngẫu nhiên, giữ trong
.payment-staging-accounts.local.json, không ghi vào Git/log; JWT chỉ ở bộ nhớ.

| Đối tượng | UUID |
| --- | --- |
| Customer | <synthetic-id-retained-locally> |
| Owner | <synthetic-id-retained-locally> |
| Vehicle | <synthetic-id-retained-locally> |
| Booking | <synthetic-id-retained-locally> |

Hai account đăng ký qua API thật. Không tạo public endpoint tự nâng role: smoke
tool chỉ thêm membership VehicleOwner Active cho đúng owner synthetic và seed
xe Available qua connection role test đã guard. Vehicle owner được kiểm tra lại.
Sau đó quote/create Booking bằng JWT Customer, thử approve bằng Customer nhận
403, approve bằng JWT Owner nhận 200. Kết quả AwaitingPayment, unit price 650000,
quoted amount **1300000 VND**, thuê hai ngày, ngày bắt đầu cách ngày chuẩn bị 14 ngày.
Account/vehicle/booking được đưa vào public.payment_test_manifest. Không sửa dữ
liệu test của các lượt integration trước hoặc account thật.

GET /bookings/mine có JWT thấy đúng Booking. POST /payments trả
503 PAYMENT_DISABLED; booking này có **0 Payment attempts**. Không tạo checkout
URL ký bằng key giả. Role owner có thêm Customer từ quy trình đăng ký mặc định;
đăng nhập lại sau cấp role đã nhận claim VehicleOwner thật.

Scope Auth smoke là email/password. Google không được dùng; staging FE ép bỏ
Google client id. Không khẳng định toàn bộ Google/refresh/suspended-role flow đã
được audit trong 2F.1. Các account cũ của integration có dummy hash không được
dùng để đăng nhập. Giữ dataset/credential local để người dùng inspect và chuẩn bị
E2E; không cleanup, không đổi trạng thái Booking để ép thanh toán lại.

## Đối chiếu VNPay chính thức

Đã đọc [tài liệu PAY chính thức](https://sandbox.vnpayment.vn/apis/docs/thanh-toan-pay/pay.html):
gateway dùng URL sandbox, HMAC-SHA512 trên tham số sắp xếp, amount nhân 100,
ngày GMT+7; IPN GET yêu cầu SSL và dùng để cập nhật kết quả. Return phục vụ trình
duyệt. Có thể đăng ký merchant tại [devreg](https://sandbox.vnpayment.vn/devreg/).

VnPayGateway hiện có phù hợp các điểm trên: version 2.1.0, pay/VND, UUID reference,
TTL, canonical form encoding và HMAC; verify merchant/signature/amount/reference/
transaction number/time, rồi service/repository kiểm tra Booking/receipt. Return
chỉ đọc và redirect; IPN mới là đường ghi. Các unit vector và PG integration đã
kiểm tra code, **chưa chứng minh kết nối merchant thật hoặc hợp đồng ngân hàng**.

Thông tin còn thiếu phải được đăng ký/cấu hình qua secret local, không gửi trong chat:

| Setting | Cần bổ sung |
| --- | --- |
| VnPay__TmnCode | Mã merchant sandbox thật do VNPay cấp; code hiện kiểm tra 8 alnum |
| VnPay__HashSecret | Key sandbox thật do VNPay cấp; không đưa vào VITE_*, Git hoặc log |
| VnPay__PaymentUrl | Đã cố định https://sandbox.vnpayment.vn/paymentv2/vpcpay.html |
| PaymentStaging__PublicOrigin | HTTPS origin của hostname staging riêng, không phải production |
| PaymentStaging__IpnUrl | Origin trên + /api/payments/vnpay/ipn; đăng ký riêng với VNPay |
| VnPay__ReturnUrl | Origin trên + /api/payments/vnpay/return/redirect |
| PaymentReturn__FrontendResultUrl | Origin trên + /payment/result, cố định từ config |
| AllowedHosts | Thêm đúng hostname staging khi có reverse proxy, không mở wildcard |
| PaymentStaging__CallbacksVerified | Chỉ true sau khi TLS/routing/IPN truy cập được từ ngoài |
| PaymentStaging__MerchantVerified | Chỉ true khi người vận hành xác nhận credential thuộc merchant sandbox thật |
| Payment__Enabled | Hiện false; không tự bật khi chỉ điền đủ định dạng |

IpnUrl là thông tin readiness/đăng ký, không phải tham số checkout do gateway tự
đăng ký với VNPay. Hai cờ Verified là xác nhận vận hành, không phải một API đã tự
chứng thực merchant. App không thể xác nhận tính thật của HashSecret chỉ từ độ dài.
Nếu bật Payment mà thiếu các gate/URL HTTPS đồng nhất, startup từ chối. Hiện vẫn
để trống TmnCode/HashSecret/ReturnUrl/public origin và các gate=false.

## HTTPS riêng — mới là phương án, chưa triển khai

Ưu tiên một hostname staging riêng với TLS hợp lệ, proxy cùng origin:
- /api/* và /health → API loopback 127.0.0.1:5288.
- Các path khác → built frontend dist-payment-staging, fallback index.html.
- IPN/Return dùng path cố định ở bảng trên; không URL từ query string.

deployment/payment-staging.Caddyfile.example là template chưa chạy, cần hostname
thật và thư mục built FE tuyệt đối. Không sửa DNS/routing/domain production,
không mở PostgreSQL qua proxy. Trước khi dùng, review access/error logs để redact
callback query, không bật debug/body/credential logs, kiểm tra Host và proxy trust.
IPN phải truy cập được không qua trang đăng nhập/challenge; chữ ký và dữ liệu
giao dịch vẫn bắt buộc được backend xác minh. Không dùng IP allowlist thay chữ ký.
Chưa cấp chứng chỉ, đăng ký callback hay mở public tunnel trong đợt này.

## Kiểm tra đã chạy

- Backend Release build: 0 warning/error.
- Unit/API/safety: **167 passed, 0 failed, 40 skipped** (156 cũ + 11 staging safety).
  DB opt-in và smoke bị tắt trong lượt này; 24 write groups của 2E.2 không chạy lại
  khi API staging đang hoạt động để tránh tranh connection.
- Smoke riêng với consent: **1 passed**, HTTP thật Auth/Booking/JWT/CORS, role test
  thật, Payment tắt, không mock gateway/provider success.
- FE unit/UI: **63 passed**; typecheck chung và staging riêng passed; staging build
  thành công. Bổ sung @types/node dev-only để kiểm tra tooling TS, npm audit 0 issue
  ở lần install; không thay dependency runtime.
- Browser staging: **4 passed**, desktop/mobile. Login thật qua FE proxy, mine thật,
  POST Payment thật trả 503, không redirect; direct /payment/result và reload giữ
  ID, hiển thị chưa khả dụng thay vì tin status=Succeeded giả trong query.
- Browser chặn mọi host ngoài loopback; không trace/video/storage-state/token file.
  Password không dùng làm literal trong locator logs; clear input khi có failure.
- CORS cho origin 127.0.0.1:5289 hoạt động; origin không tin cậy không được cấp
  Access-Control-Allow-Origin. Proxy FE /health trả đúng environment/database.
- GET IPN local khi flag tắt: HTTP 200/RspCode 99. Return redirect local: HTTP 302
  về http://127.0.0.1:5289/payment/result?returnError=unavailable. Đây chỉ là kiểm
  tra route unavailable, không phải callback VNPay thật hoặc xác minh chữ ký E2E.
- git diff --check: không có lỗi. Secret config/accounts đều được xác nhận ignored.

API lúc đầu đã dừng an toàn khi default JSON file provider không thấy dotfile;
đã sửa loader explicit. Frontend preview ban đầu ở sandbox network không truy
cập được từ host; đã restart cùng môi trường local với API, proxy/browser sau đó
passed. Không có fallback trỏ sang production để khắc phục các lỗi này.

## Cách chạy lại local

Backend (PowerShell tại BonBon-Backend):
1. ./tools/Initialize-PaymentStaging.ps1 (không overwrite file đã có).
2. dotnet build BonBon.sln --no-restore -c Release (dừng instance API trước rebuild).
3. ./tools/Start-PaymentStaging.ps1 — giữ terminal này, Ctrl+C để dừng.

Frontend tại BonBon-Frontend: npm run build:payment-staging rồi
npm run preview:payment-staging; Ctrl+C terminal để dừng. Vite profile staging
ép API=/api và proxy loopback 5288; không dùng VITE_API_URL production trong .env.
Output dist-payment-staging riêng, không overwrite dist của build thường. Không
deploy các file secret, accounts hoặc source test helper như API endpoint.

Để provision/smoke lại chỉ khi vẫn được phép ghi test DB: đặt
BONBON_PAYMENT_STAGING_CONFIG thành path tuyệt đối secret config và
BONBON_PAYMENT_STAGING_PREPARE=I_CONFIRM_LOCAL_STAGE_TEST_DB_ONLY, rồi chạy đúng
PaymentStagingSmokeTests. Các biến enable fixture cũ/PG integration phải xóa.
Browser: BONBON_STAGING_ACCOUNTS_FILE trỏ file accounts ignored,
BONBON_STAGING_BROWSER_CONSENT=I_CONFIRM_LOCAL_STAGE_TEST_DB_ONLY rồi
npm run test:payment-staging. Không có opt-in thì các test này skip.

## Điều kiện còn thiếu

Chưa sẵn sàng thực hiện giao dịch gateway: thiếu merchant sandbox và HTTPS public.
Ngoài hai điều kiện đó cần chốt kế hoạch theo dõi/đối soát các receipt chưa rõ:
QUERYDR/worker, refund và TTL giữ chỗ Booking chưa triển khai đầy đủ. Pending/
Processing hết hạn không tự coi là thất bại, không tự mở attempt mới hoặc giải
phóng lịch; không sửa SQL/status để bỏ chốt. E2E sau này phải có phạm vi và cách
xử lý thủ công được chấp thuận khi chưa có worker; không mặc nhiên bật flag.

Không có ALTER/migration hoặc INSERT/UPDATE/DELETE/DROP vào database bonbon trong
2F.1. Staging/smoke chỉ kết nối database test bằng role test; không dùng credential
bonbon_app. Không thay website production, không dùng tiền/thẻ thật, không gửi
request giao dịch hoặc callback giả thành công đến gateway.

## File thay đổi 2F.1

Backend:
- BonBon.API/Program.cs — nhánh environment/DS/readiness/CORS/proxy riêng.
- BonBon.API/Configuration/PaymentStaging.cs — mới, guard/config/physical initializer.
- BonBon.Tests/Payments/PaymentStagingSafetyTests.cs — mới, 11 tests offline.
- BonBon.Tests/Integration/PaymentStagingSmokeTests.cs — mới, opt-in HTTP/provision.
- tools/Initialize-PaymentStaging.ps1, tools/Start-PaymentStaging.ps1 — mới.
- deployment/payment-staging.Caddyfile.example — mới, chưa dùng để deploy.
- .gitignore — ignore secret config/accounts staging.
- docs/payment-staging-2f1.md — báo cáo này.

Frontend:
- vite.payment-staging.config.ts — mới, target/output riêng.
- playwright.payment-staging.config.ts, tests/staging/payment-local.spec.ts — mới.
- tsconfig.payment-staging.json — mới, typecheck cả tooling/browser test mới.
- package.json/package-lock.json — scripts staging và @types/node dev dependency.
- .gitignore — bỏ qua dist-payment-staging.

Hai file local .payment-staging.local.json và .payment-staging-accounts.local.json
được tạo và giữ lại ngoài Git. Không ghi giá trị secret trong báo cáo này.
