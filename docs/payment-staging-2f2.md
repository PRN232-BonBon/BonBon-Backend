# 2F.2 — HTTPS staging và merchant VNPay sandbox

## Trạng thái và phạm vi

Merchant VNPay sandbox đã được người dùng đăng ký và kích hoạt, đã nhận TmnCode và
HashSecret thật. Credential đã được người dùng lưu và kiểm tra bind/định dạng bằng
đúng bộ nạp cấu hình .NET; không in giá trị, không gọi VNPay để xác thực merchant.
Chưa công khai dịch vụ hoặc xác minh hostname HTTPS. Payment vẫn tắt.
Không tạo Payment attempt, không thực hiện giao dịch, không chạy migration. Không kết nối
database chính `bonbon` trong bước kiểm tra này. Staging dùng duy nhất database
`bonbon_payment_test`, role `bonbon_payment_test_runner`, pool tối đa 2/role limit 4.

## Phương án HTTPS chờ phê duyệt

`deployment/payment-staging.Caddyfile.example` là bản đề xuất cho **Caddy riêng chạy
trực tiếp trên máy staging**, có DNS staging riêng, TLS công khai hợp lệ trên cổng 443.
API vẫn bind `127.0.0.1:5288`; Caddy phục vụ bản build `dist-payment-staging` trực tiếp.
Không cần công khai Vite 5289, API 5288, PostgreSQL 5432 hoặc mạng Tailscale.
Không dùng hostname `bonbon.kitsuracloud.com` hoặc hostname con của nó.

Các biến proxy cần chuẩn bị cục bộ:

| Biến | Giá trị cần có |
|---|---|
| `PAYMENT_STAGING_HOST` | Hostname DNS staging đã được phê duyệt, không scheme/path/port |
| `PAYMENT_STAGING_FRONTEND_ROOT` | Đường dẫn tuyệt đối `BonBon-Frontend/dist-payment-staging` |
| `PAYMENT_STAGING_OPERATOR_CIDRS` | IP công khai của từng tester (`/32` hoặc `/128`), không `/0` |

Route policy:

| Route | Truy cập |
|---|---|
| GET `/api/payments/vnpay/ipn` | Công khai, không JWT/challenge; Backend xác minh callback |
| GET `/api/payments/vnpay/return/redirect` | Công khai, chỉ xác minh/điều hướng |
| GET `/api/bookings/mine`, GET `/api/payments/{UUID}` | Chỉ IP tester, vẫn phải có JWT |
| POST `/api/auth/login`, `/refresh`, `/logout` | Chỉ IP tester, tài khoản synthetic |
| GET/HEAD `/`, `/my-trips`, `/payment/result`, `/assets/*`, favicon | Chỉ IP tester; SPA fallback |
| Mọi route khác | 404; bao gồm POST `/api/payments`, `/health`, Swagger/admin/debug/test, đăng ký/Google |

Trong 2F.2 chặn POST tạo Payment ngay cả nếu cấu hình API bị bật nhầm. Chỉ thêm allowlist
POST `/api/payments` sau review và chấp thuận 2F.3. Tester phải dùng IP đã khai báo cả
khi trở về từ VNPay; người khác có thể bị 404 trên trang kết quả. Không dùng Basic Auth
trên Payment API vì cùng header Authorization với JWT.

Không cấu hình wildcard proxy `/api/*`, wildcard operator CIDR hoặc trusted proxy toàn mạng.
Callback override X-Forwarded-For bằng remote socket và X-Forwarded-Proto=https;
Caddy mặc định không tin X-Forwarded-* do client gửi. API chỉ tin loopback, một hop,
yêu cầu hai header đồng bộ và không xử lý X-Forwarded-Host. Cấu hình `AllowedHosts`
phải gồm hostname staging cụ thể (có thể thêm localhost/127.0.0.1), không `*`.
Không ghi access log; runtime/error log vẫn phải bảo vệ, không bật debug/copy URL có chữ ký.

Máy hiện chưa có Caddy nên **chưa chạy caddy validate hoặc xác minh TLS/route policy
trên proxy thật**. Trước khi khởi động phải cài Caddy bản chính thức, kiểm tra cú pháp,
kiểm tra các biến host/IP/root, firewall và DNS. `caddy adapt`/`validate` chỉ được chạy
với cấu hình local đã điền, không đưa secret vào stdout. Khởi động Caddy hoặc cấu hình
DNS/firewall/public tunnel cần xác nhận riêng của người dùng cho hostname và phạm vi route.

Nếu máy không nhận inbound 80/443, có thể dùng named tunnel HTTPS riêng (máy có
cloudflared), nhưng **không dùng trực tiếp template edge này sau tunnel**: remote_ip
sẽ là connector, làm sai allowlist tester. Cần cấu hình policy ở edge, bypass xác thực
chỉ cho hai callback, xác minh identity/trusted proxy đúng nhà cung cấp và origin
loopback riêng. Không chạy quick tunnel, login, DNS hoặc mở listener trong 2F.2.

## Merchant và secret local

Security review trước public push: bắt buộc `PaymentStaging.ExpectedDatabaseHost`
trong JSON secret local (hoặc `PaymentStaging__ExpectedDatabaseHost` trong process
environment riêng staging). Đây là pin target độc lập, không được lấy tự động từ
connection string và không công khai hostname thật. Mọi identity/marker/role guard
vẫn bắt buộc; file cũ thiếu pin phải được quản trị viên bổ sung trước khi khởi động.

File hiện có: `BonBon.API/.payment-staging.local.json` (đường dẫn tuyệt đối trong
workspace hiện tại: `<workspace>\BonBon-Backend\BonBon.API\.payment-staging.local.json`).
File đã được Git ignore và không được Git theo dõi. Mở bằng editor local, chỉ điền
hai khóa `VnPay.TmnCode` và `VnPay.HashSecret`, giữ nguyên các khóa khác và
`Payment.Enabled=false`. Tên `VnPay__TmnCode`/`VnPay__HashSecret` chỉ dùng cho biến
môi trường; không thêm các khóa có `__` vào JSON. Không gửi credential vào chat.
Sau khi người dùng báo đã lưu mới kiểm tra cấu hình hiệu lực, chỉ báo đạt/chưa đạt.
Backend nạp file qua `BONBON_PAYMENT_STAGING_CONFIG`, environment ghi đè file;
`reloadOnChange=false` nên tiến trình đang chạy cần được khởi động lại để nhận giá trị mới.

Kiểm tra thực tế sau khi lưu: `PaymentStagingMerchantLocalTests` opt-in
`BONBON_PAYMENT_MERCHANT_LOCAL_CHECK=READ_CONFIG_ONLY` đã passed. Test chỉ mở file,
bind `VnPayOptions`, kiểm tra định dạng TmnCode/HashSecret, gateway sandbox,
không có environment ghi đè hai credential khác giá trị file, Payment tắt và guard
cấu hình target testDB. Không mở kết nối DB hoặc gọi gateway. Không coi đây là bằng
chứng merchant đã được VNPay xác thực bằng một giao dịch. API staging đã restart,
health xác nhận đúng testDB/guard và Payment tắt.

Phát hiện Web SDK tự copy secret JSON vào build output mặc dù đã Git ignore.
`BonBon.API.csproj` đã loại `.payment*.local.json` khỏi Content/None build/publish.
Đã dọn 12 bản sao được sinh trong bin/obj của API/Tests, giữ nguyên file nguồn local;
build lại xác nhận không còn bản sao. Không mở/in nội dung bản sao.

## Lựa chọn hostname khi chưa có domain riêng

Người dùng chọn ưu tiên hostname cố định do tunnel cấp. Phương án phù hợp là
ngrok Dev Domain gắn với tài khoản (https://ngrok.com/docs/gateway/domains).
Tạo/đăng nhập tài khoản qua dashboard, xem mục Domains và lấy đúng Dev Domain được
cấp, không tự bịa hostname. Chưa chạy agent, chưa tạo public endpoint hoặc thêm
authtoken qua command history. Ngrok chưa được cài trên máy tại thời điểm kiểm tra.

Ngrok Free có browser warning/interstitial cho HTML browser traffic; tài liệu nói
API/programmatic traffic không bị ảnh hưởng (https://ngrok.com/docs/pricing-limits/free-plan-limits).
Vẫn phải kiểm chứng callback nhận JSON
trực tiếp, không challenge/HTML, với kiểu request thực tế VNPay. Không giả định
VNPay sẽ tự gửi header bỏ qua warning. Nếu không đáp ứng thì cần phương án/gói
phù hợp, không tự mua hoặc bỏ qua chốt. Cloudflare named tunnel với hostname riêng
cần domain đã quản lý; quick tunnel hostname ngẫu nhiên không phù hợp IPN đăng ký ổn định.

Không trỏ tunnel thẳng vào5288 hoặc5289: sẽ mở toàn bộ API hoặc Vite proxy bypass
allowlist. Cần gateway local loopback riêng và route policy tại edge: hai callback
GET công khai không JWT/IP tester/challenge; route human chỉ IP/identity tester,
API vẫn JWT; mọi route còn lại từ chối. Template Caddy hiện tại chỉ cho edge trực
tiếp, không dùng nguyên sau ngrok: socket client trở thành agent, remote_ip không
còn IP tester và X-Forwarded-Proto có thể bị đổi thành HTTP. Cần cấu hình ngrok/edge
riêng, giới hạn/chuẩn hóa forwarded headers và tắt capture request/response nhạy cảm
trước khi xin phê duyệt public endpoint. Chưa áp dụng hoặc công khai cấu hình này.

Sau khi có hostname thật H, trong JSON local cùng lúc điền:

| Khóa JSON | Giá trị |
|---|---|
| `PaymentStaging.PublicOrigin` | `https://H` |
| `PaymentStaging.IpnUrl` | `https://H/api/payments/vnpay/ipn` |
| `VnPay.ReturnUrl` | `https://H/api/payments/vnpay/return/redirect` |
| `PaymentReturn.FrontendResultUrl` | `https://H/payment/result` |
| `AllowedHosts` | `H;localhost;127.0.0.1` |

H chỉ là ký hiệu hướng dẫn, không lưu placeholder vào cấu hình. Giữ Payment.Enabled,
CallbacksVerified, MerchantVerified và ReconciliationPlanVerified hiện tại; không tự bật
các flag chỉ vì bind credential đã đạt. Không cần thay API base URL Frontend `/api`.

Tài liệu VNPay nói TmnCode tương ứng website/domain merchant. Chưa biết website đã
khai lúc đăng ký, nên chưa thể kết luận H được merchant chấp nhận. Người dùng đối
chiếu thông tin đăng ký/email activation/dashboard; gửi đầu mối hỗ trợ sandbox VNPay
hostname staging H, IPN URL đầy đủ và Return URL để xác nhận/cập nhật merchant đã
kích hoạt. Không gửi HashSecret. Không giả định có màn hình tự đổi IPN nếu tài liệu
hoặc tài khoản không cung cấp. Tài liệu chính thức yêu cầu gửi IPN URL cho VNPay khi
thiết lập xong; việc khai local không tự đăng ký IPN trên VNPay.

Kết quả bổ sung sau khi nhập credential: `dotnet test -c Debug` build thành công,
181 passed, 0 failed, 41 skipped; opt-in duy nhất là kiểm tra cấu hình merchant local,
không chạy fixture PostgreSQL. Health sau restart xác nhận PaymentStaging/testDB/
database guard đạt và Payment tắt. Không gửi giá trị secret, hash, chữ ký hoặc token
ra terminal/log. Không tạo Payment attempt, không gọi VNPay, không công khai tunnel.

Tham khảo đăng ký merchant sandbox qua https://sandbox.vnpayment.vn/devreg/ theo tài liệu
https://sandbox.vnpayment.vn/apis/docs/thanh-toan-pay/pay.html . Lấy TmnCode và
HashSecret **do VNPay cấp**, không dùng dữ liệu unit test. URL gateway được cố định
`https://sandbox.vnpayment.vn/paymentv2/vpcpay.html`.

Đặt secret trong process environment của riêng staging bằng công cụ secret local hoặc
chỉnh file JSON staging đã gitignore bằng editor riêng; không gõ secret vào command
history, chat, log, `.env.example`, Git hoặc frontend. Environment ghi đè file local.
Không dump toàn bộ config/env khi chẩn đoán. Chỉ báo có/không, không báo giá trị bí mật.

Với origin `https://<hostname-staging-đã-phê-duyệt>`:

| Cấu hình | Giá trị |
|---|---|
| `VnPay__TmnCode` | Merchant sandbox thật |
| `VnPay__HashSecret` | Secret sandbox thật, chỉ Backend |
| `VnPay__PaymentUrl` | Gateway sandbox cố định ở trên |
| `PaymentStaging__PublicOrigin` | Origin HTTPS staging, không path/query/userinfo |
| `VnPay__ReturnUrl` | Origin + `/api/payments/vnpay/return/redirect` |
| `PaymentStaging__IpnUrl` | Origin + `/api/payments/vnpay/ipn` |
| `PaymentReturn__FrontendResultUrl` | Origin + `/payment/result` |
| `AllowedHosts` | Hostname staging cụ thể;localhost;127.0.0.1 |
| `Payment__Enabled` | `false` trong toàn bộ giai đoạn này |
| `PaymentStaging__MerchantVerified` | `false` đến khi xác nhận merchant sandbox thật |
| `PaymentStaging__CallbacksVerified` | `false` đến khi xác minh HTTPS và đăng ký IPN |
| `PaymentStaging__ReconciliationPlanVerified` | `false` đến khi chấp thuận kế hoạch xử lý giao dịch treo |

Không chỉ đặt `PaymentStaging__IpnUrl` rồi coi là đăng ký: gateway hiện không gửi URL
IPN trong checkout. Cung cấp chính xác URL IPN HTTPS cho bộ phận hỗ trợ/đầu mối tích
hợp VNPay để cấu hình merchant theo tài liệu; xác nhận lại URL và khả năng gọi từ
Internet. Không tự gửi email hoặc thông tin merchant thay người dùng.

## Return, JWT, CORS và dữ liệu

Return chỉ Verify HMAC-SHA512/merchant/reference/amount và đọc giao dịch. Nếu hợp lệ,
302 về destination cố định với duy nhất paymentId đã resolve từ DB. Không truyền
signature/status VNPay sang Frontend, không cập nhật Succeeded; lỗi chỉ truyền
returnError. Với Payment tắt trả 302 `?returnError=unavailable`, không thành công giả.
Frontend dùng GET trạng thái với JWT của Customer, không tin query status. Không có
JWT trả 401. Frontend staging build dùng `/api` cùng origin; CORS chỉ nhận origin staging
(hoặc localhost5289 khi chưa có PublicOrigin), không thừa hưởng origin production.
IPN mới gọi transaction cập nhật Payment/Booking/audit, sau xác minh HMAC, merchant,
reference, số tiền; policy xử lý duplicate/late/conflict và reconciliation.

## Chốt trước 2F.3

1. Có hostname, IP tester, phương án HTTPS và xác nhận công khai đúng phạm vi.
2. Cài/validate proxy, chứng chỉ TLS công khai, kiểm tra route bị chặn từ IP ngoài;
   xác minh callback qua Internet. Không thử callback giả có chữ ký hợp lệ vào dữ liệu thật.
3. Merchant sandbox và IPN đã đăng ký; cấu hình đồng nhất; secret chỉ ở Backend.
4. Khởi động staging mới với guard kiểm tra current_database/current_user/session_user,
   owner, marker testDB, PostgreSQL16.15, role hạn chế/limit4 và marker/schema004/005.
5. QUERYDR, worker đối soát/refund và TTL tự giải phóng Booking **chưa triển khai**.
   Checkout TTL15 phút không phải Booking TTL. Không tự tạo attempt mới sau timeout,
   không tự xác nhận hoặc giải phóng booking khi kết quả chưa rõ. Cần quy trình người
   phụ trách tra soát merchant/server-side và quyết định có giới hạn phạm vi E2E không;
   không tự đặt flag ReconciliationPlanVerified=true để vượt chốt.
6. Review mở POST payment cho IP tester riêng trong2F.3, bật Payment chỉ staging sau
   tất cả kiểm tra; không thay đổi config/website/database production.

## Kiểm tra an toàn

`PaymentStagingReadinessTests` opt-in `BONBON_PAYMENT_STAGING_READ_ONLY=I_CONFIRM_LOCAL_READ_ONLY`
và đường dẫn tuyệt đối `BONBON_PAYMENT_STAGING_CONFIG`. Probe xác minh guard, chạy
SELECT trong transaction READ ONLY, timeout5s/lock1s; kiểm tra Booking hiện có vẫn
AwaitingPayment/1300000VND/0attempt trước và sau; GET/OPTIONS health, Return302,
JWT401, IPN unavailable, CORS và SPA direct navigation. Không POST Auth/Payment,
không provision/cleanup dữ liệu. Không bật fixture prepare hoặc write integration.

Kết quả 2F.2:

- Backend Release build: thành công, 0 warning/0 error.
- Bộ unit/API mặc định: 180 passed, 0 failed, 41 skipped (mọi opt-in DB/prepare tắt).
- Probe staging chỉ đọc chạy riêng: 1 passed, 0 failed; đúng testDB/role/marker,
  Booking AwaitingPayment/1300000VND, 0 attempt trước/sau; Return302 cố định,
  JWT401, IPN99 khi tắt, CORS chỉ origin local, SPA200, Swagger404.
- Frontend build thường và payment-staging, typecheck staging: thành công;
  Vitest63 passed, browser regression36 passed (desktop/mobile, API intercept).
- Hai listener thực tế chỉ bind127.0.0.1:5288/5289; API được khởi động lại sau build.
- `git diff --check` không có lỗi; hai repo vẫn feature/payment, không commit/push.

Build đầu bị khóa DLL bởi API staging đang chạy; đã dừng đúng API rồi build thành công.
Vitest trong sandbox gặp EPERM thư mục tạm; chạy lại ngoài sandbox thành công.
Không chạy lại test prepare/write integration. Không tạo/sửa/xóa bản ghi hoặc schema.
Proxy chưa được cài/validate; TLS, IPN Internet, merchant thực tế vẫn pending,
không thay bằng mock hoặc unit test. Chỉ kiểm chứng Return302 nhánh unavailable
trên staging thật; nhánh paymentId hợp lệ được regression bằng unit/browser test,
chưa chứng minh bằng giao dịch VNPay thật.
