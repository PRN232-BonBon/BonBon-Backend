# VNPay sandbox web — review và runbook cho nhóm

Cập nhật sau LOCAL SIMULATION: xem `VNPAY_REAL_SANDBOX_READINESS.md` để biết trạng thái
merchant/HTTPS hiện tại, chốt không public simulator và checklist giao dịch VNPay thật.
Các số test và trạng thái chưa chạy SQL ở phần dưới là snapshot lượt review ban đầu;
không thay thế báo cáo kiểm thử local và readiness mới.

Ngày:15/11/2026. Source review trên `review/payment-sandbox-web` ở cả hai repo,
tạo từ origin/main (Backend7ba89bc,Frontend5f00959). Không merge/cherry-pick nguyên
frontend feature/payment vì main có UI đặt xe, popup thanh toán, owner inbox và tracking mới.
Không deploy, SSH, migrate, bật production hoặc thực hiện giao dịch trong lượt này.

## 1. Kết luận so sánh source

| Thành phần | main so với feature/payment |
|---|---|
| Backend Payment/Approval | Commit Backend Payment là ancestor của main; controller/service/gateway/repository/policy và004/005 đã tích hợp; không ghi đè |
| Frontend UI | main dùng PaymentModal/portal PaymentResultPage, OwnerBookingsPage và UI Booking mới; giữ nguyên layout/luồng |
| Key idempotency | main cũ dùng sessionStorage+memory fallback; nay lưu localStorage trước POST, migrate session key cũ, fail closed khi hỏng/storage bị chặn |
| Response | main cũ ép kiểu JSON; nay kiểm UUID, booking/reference, số tiềnVND, status, expiry và reconciliation boolean; không suy thành công từ dữ liệu thiếu |
| Return | Dùng Backend302 về URL cấu hình cố định với paymentId; reject ID trùng/query returnError; không dùng raw VNPay query để báo success |
| Polling/switch account | main cũ có thể poll tiếp qua visibilitychange sau terminal/error; nay tối đa12 lần/5s, manual1 lần, lỗi dừng, receipt và Trips scope theo account |
| Error/gateway | Ánh xạ401/403/404/409/503 tiếng Việt, không phát detail nội bộ; URL chỉ đúng gateway sandbox, không fragment/userinfo/host giả |
| Snapshot mới từ006 | Proposal005 trước đây chưa khóa discount/mileage/terms; nay so sánh optional fields qua JSONB, vẫn dùng được baseline cũ; không so generated column trong BEFORE trigger |

Không mang UI/CSS cũ hoặc toàn bộ nhánh feature vào main. Legacy Return JSON vẫn có
ở Backend nhưng UI hiện tại dùng redirect khuyến nghị; không cần khôi phục đường raw
VNPay query trực tiếp vào Frontend. Payment button/modal chỉ mở trên booking được duyệt;
Backend vẫn kiểm Customer thực sự, active roles/owner/vehicle, snapshot từ DB.

## 2. Những gì đã có trong luồng

Customer quote/create → PendingApproval → owner thực sự approve → AwaitingPayment →
POST payments với JWT+key → Pending/checkout URL ký HMAC-SHA512 → IPN hợp lệ →
audit+Payment Succeeded+Booking Confirmed cùng transaction → JWT GET status → UI success.
Reject chỉ từ PendingApproval; replay cùng quyết định idempotent. Giá từ quoted_rental_amount,
không từ client. Callback duplicate xử lý fingerprint/transaction unique; receipt bất thường
giữ audit/reconciliation, không tự Confirmed. Return không ghi DB và không xác nhận tiền.

## 3. Mẫu staging đã chuẩn bị, chưa triển khai

- `deployment/compose.payment-sandbox.example.yaml`: project riêng bonbon-payment-sandbox,
  chỉ service API/image immutable được duyệt; không extends compose production, không
  service postgres/init script, không mount production data/admin. Bind API127.0.0.1:5288.
- `deployment/payment-sandbox.settings.example.json`: template trống credential/JWT/DB,
  Payment=false và ba readiness flags=false. Copy tới file private `.payment-staging.local.json`
  ngoài Git; không chạy nguyên placeholder. Không đưa secret vào VITE_*, terminal/chat/log.
- `deployment/payment-sandbox.Caddyfile.example`: hostname staging riêng cùng origin
  cho SPA và /api; chỉ callback GET không JWT/IP restriction; human routes giới hạn IP tester,
  Backend vẫn kiểm JWT. Không admin/debug/tracking/health công khai.
- Frontend `vite.payment-sandbox.config.ts`: builddist-payment-sandbox với API=/api,
  Google client tắt để không lấy target production từ .env cục bộ.

Ví dụ hostname trong JSON là miền `.test` giả cho tài liệu, không phải dịch vụ đã tạo.
Admin thay đồng thời PublicOrigin,IpnUrl,ReturnUrl,FrontendResultUrl,AllowedHosts bằng
hostname HTTPS staging thực tế thuộc quyền quản lý. Không dùng các hostname production.
Chỉ một origin staging phù hợp guard hiện có; không tự tách frontend/api khác host rồi
bỏ guard. Không chạy Caddy thứ hai tranh80/443; admin review/import vhost vào edge
hiện có hoặc máy staging riêng. Template dành cho direct edge, không dùng sau tunnel/CDN
mà chưa chỉnh trust/client-IP và giới hạn tester đúng cách. Chưa chạy Caddy validate.

Docker bridge làm socket source khác loopback: admin xác định IP proxy thực tế nhìn
từ container và điền **chính xác** `PaymentStaging.TrustedProxyAddresses` trong private
JSON; không dùng wildcard/CIDR hoặc trust mọi private network. API giữ forward limit1,
header symmetry, loopback và exact allowlist; Caddy không tin X-Forwarded-* từ client.
Các source IP cụ thể không được đưa vào Git. CORS staging lấy đúng PublicOrigin.

## 4. Checklist quản trị viên — theo thứ tự, không tác động production

### A. Quyền truy cập và database

1. Xác minh host key từ console quản trị/kênh độc lập trước SSH. Fingerprint từ kết nối
   hoặc keyscan chỉ là quan sát. Không chạy workflow_dispatch production để test nhánh này.
2. Chọn máy/project/image riêng. Kiểm tra port5288 chưa được dùng, source/tag đúng code
   review, bao gồm thay đổi TrustedProxyAddresses của nhánh này; image main hiện tại
   chưa có thay đổi đó. Không thay service/image/volume của project bonbon hiện tại.
3. DBA xác minh database **bonbon_payment_test**, owner/login **bonbon_payment_test_runner**,
   NOSUPERUSER/NOCREATEDB/NOCREATEROLE, role limit4, pool2 và không có quyền ghi mainDB.
   Pin host độc lập trong privateconfig, không suy ra từ connection string. Guard còn yêu
   cầu PostgreSQL16.15, database marker `bonbon-payment-test:v1:<approved-server-id>`,
   control stage=ready, payment marker/audit/indexes và quote guard. Không đổi marker
   hoặc bỏ guard chỉ để khởi động; nếu server khác phải có review môi trường riêng.
4. Test DB cần **schema tương thích main mới**, không chỉ schema2E.2 cũ:006 có discount/
   mileage/terms snapshots, create_booking() mới và catalog/registration/review fields.
   Clone schema không dữ liệu khách thật, hoặc xây baseline trên server disposable và
   đối chiếu metadata. Không chạy001→002→003 máy móc; không chạy demo seeds lên mainDB.
5. Review/full-name proposals004_vnpay_sandbox.sql và005_booking_quote_guard.sql.
   Migration004_minimal_vehicle_details/005_demo_vehicle_catalog KHÔNG thay thế Payment004/005.
   Có file SQL không chứng minh đã apply. Kiểm tra cột/index predicates/CHECK/FK/function
   body thực tế.005 đã sửa snapshot006 trong review này, chưa xác minh PostgreSQL thật;
   test trên DB cô lập trước. Nếu005 cũ đã áp dụng, DBA quản lý roll-forward/version mới
   theo diff; không âm thầm sửa lịch sử hoặc chạy lại004 one-shot.
6. Chỉ sau backup/recovery và phê duyệt target test, DBA apply schema/proposal trên DB
   test. Lỗi giữa004/005: giữ Payment tắt. Không DROP/rebuild DB có dữ liệu không nhận diện,
   không dùng disposable fixture trên server nhóm (fixture loopback có CREATE/DROP).

Kiểm tra metadata ban đầu bằng role/connection đúng target, chỉ đọc:

```sql
BEGIN READ ONLY;
SET LOCAL statement_timeout='5s';
SET LOCAL lock_timeout='1s';
SELECT current_database()='bonbon_payment_test' AS test_database,
       current_user='bonbon_payment_test_runner' AS test_role;
SELECT to_regclass('bonbon.payment_gateway_schema') IS NOT NULL AS payment_marker,
       to_regclass('bonbon.payment_notification') IS NOT NULL AS audit_table;
SELECT count(*)=4 AS unique_indexes_ready FROM pg_index i
JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
  AND c.relname IN('uq_vnpay_sandbox_active','uq_vnpay_sandbox_idempotency',
                  'uq_vnpay_sandbox_accepted_success','uq_vnpay_sandbox_transaction');
ROLLBACK;
```

Đây chỉ là preflight, không thay thế kiểm tra đầy đủ schema/marker/grants/function
và generated expression sau006: quoted total có trừ discount snapshot.

### B. Private config và HTTPS

7. Copy JSON mẫu vào privatepath, file không readable công khai. Bind read-only trong
   container. Image chạy nonroot: cấp quyền đọc riêng cho đúng service UID, không chmod777.
   Compose bind mount không tự remap owner; không giả định file600/root-owned đọc được
   bởi app. Không chạy docker compose config/dump inspect/env ra console vì chứa secret.
8. Điền ConnectionStrings.DefaultConnection đúng testDB/role, Pooling=true,MaxPoolSize=2,
   MinPoolSize=0,IncludeErrorDetail=false,LogParameters=false; giữ multiplexing/noreset=false.
   Jwt key riêng>=64 ký tự, issuer BonBon.PaymentStaging, audience BonBon.PaymentStaging.Web.
   ExpectedDatabaseHost phải là pin đã phê duyệt, không wildcard/multi-host.
9. Điền merchant sandbox thật vào VnPay.TmnCode/HashSecret chỉ trong secret JSON;
   không dùng credential unit test. PaymentUrl chỉ gateway sandbox cố định. Env override
   tương ứng VnPay__...; trong compose standalone mẫu nguồn chính là JSON bind mount,
   không phải .env production. Đặt PAYMENT_SANDBOX_CONFIG_FILE và PAYMENT_SANDBOX_API_IMAGE trong
   env file riêng của project sandbox; ba flags và Payment.Enabled vẫn false.
10. Admin dựng HTTPS staging riêng, frontend static root=dist-payment-sandbox; IP allowlist
    tester cố định, không /0; callback GET IPN/Return không login/challenge/JWT/IP tester.
    Không proxy PostgreSQL/Tailscale, không expose5288/8080 Internet, không route admin.
    Không dùng template2F.2 cũ còn chặn POST payments cho bài E2E; mẫu mới đã liệt kê
    POST booking/payment và approve/reject cần thiết, vẫn qua JWT Backend.
11. Giả sử origin staging là O: VnPay.ReturnUrl=O/api/payments/vnpay/return/redirect;
    PaymentStaging.IpnUrl=O/api/payments/vnpay/ipn;
    PaymentReturn.FrontendResultUrl=O/payment/result; AllowedHosts=hostname O+loopback.
    Gửi IPN/Return URL cho đầu mối sandbox VNPay để xác nhận hostname merchant/đăng ký
    IPN; đặt IpnUrl local không tự đăng ký ở VNPay. Không gửi HashSecret qua email/chat.

Các lệnh **chỉ dành cho admin sau phê duyệt**, chưa chạy trong review:

```bash
# Trỏ env file sandbox riêng; không dùng env file / project production.
docker compose --env-file /private/path/sandbox.env -p bonbon-payment-sandbox \
  -f deployment/compose.payment-sandbox.example.yaml config --quiet
# Khi config/DB/UID/port đều đã kiểm tra và được duyệt:
docker compose --env-file /private/path/sandbox.env -p bonbon-payment-sandbox \
  -f deployment/compose.payment-sandbox.example.yaml up -d --no-build
curl --fail --silent http://127.0.0.1:5288/health
```

Không dùng docker compose mặc định vì compose.yaml hiện thuộc project production,
có default DB bonbon và mount SQL init. Đừng copy toàn bộ env production sang staging.
Chỉ copy build artifact frontend sau review, không dùng dist-admin hoặc dist production.

### C. Preflight và một lượt sandbox có kiểm soát

12. API phải startup với environment PaymentStaging, guard verified testDB/role/schema,
    health Payment=false. Nếu guard lỗi thì dừng, không đổi sang credential production.
    Seed Customer/VehicleOwner synthetic bằng Auth API loopback/client không log token;
    DBA cấp role2/3 và xe Available/registration approved chỉ ở testDB, hoặc thực hiện
    registration/review bằng API nội bộ test. Không public register/admin để seed.
13. Kiểm tra HTTPS SPA direct/reload; login Customer/Owner, catalog và mine/owner list
    dùng đúng staging /api. Kiểm userIP forwarded đúng trước checkout. Với Payment tắt,
    Return đúng config phải302 về trang staging với returnError=unavailable, IPN JSON99;
    không phải HTML200. Test IP ngoài không vào human/admin routes.
14. Người phụ trách merchant xác nhận credential thật và IPN đăng ký; phụ trách HTTPS
    xác nhận route/TLS; người phụ trách vận hành chốt đối soát giao dịch treo/TTL. Sau đó
    mới đặt MerchantVerified/CallbacksVerified/ReconciliationPlanVerified=true trong
    config sandbox và Payment.Enabled=true **chỉ instance test** theo change được duyệt.
    Config không reload: admin quản lý việc recreate sandbox container, không restart
    production. Không đánh dấu flags true thay cho kiểm tra thực tế.
15. Customer đặt xe từ web staging, lưu bookingId; Owner inbox duyệt đúng booking;
    kiểm AwaitingPayment và giá snapshot. Customer mở PaymentModal, lưu paymentId,
    key phải giữ nguyên qua retry. Chỉ sử dụng tài khoản/thẻ/OTP test theo hướng dẫn
    VNPay sandbox của nhóm, không thẻ/tiền thật. Không tự chạy giao dịch ở lượt review này.
16. Xác minh server IPN hợp lệ, audit và receipt chỉ ghi một lần, Booking Confirmed;
    browser302 giữ paymentId, JWT GET trả Succeeded+requiresReconciliation=false rồi
    UI mới success. Đối chiếu DB test chỉ đọc và merchant sandbox. Không dựa query00
    hoặc trang quay lại để xác nhận. Sau đó mới test failure/duplicate/late có kiểm soát.
17. Nếu IPN mất/chậm, expiry/đối soát hoặc amount/reference không khớp: dừng, không tạo
    key mới/clear storage/thử thu lại. Lưu ID nội bộ an toàn, không chia sẻ signed URL,
    JWT hoặc callback signature. Người phụ trách vận hành xử lý qua merchant; không
    sửa DB flags/status để ép thành công.

## 5. Chưa triển khai / chưa xác minh

QUERYDR, worker đối soát, refund/reversal workflow và Booking TTL tự giải phóng chưa có.
Checkout TTL15 phút không phải Booking deadline. Không có admin recovery API khi mất
storage; key theo account+booking vẫn không tự rotate sau terminal failure. Cổng VNPay
production chưa hỗ trợ. HTTPS, Docker/Caddy runtime config và proposal005 sửa mới chưa
test trên PostgreSQL/hosting thật. Tất cả thuộc gate của nhóm, không tuyên bố E2E pass.

Các lỗi production đã biết PAYMENT_DISABLED và Return config invalid chưa sửa trên
server và không được giải quyết bằng cách bật production. Không cần kiểm tra lại các
endpoint đó để hoàn thiện source. SSH vẫn cần xác minh độc lập; admin có thể thực hiện
checklist trên qua console được tin cậy, không làm tắc phần code review/test local.

## 6. Test local không dịch vụ bên ngoài

- Backend: build/test với mọi BONBON_TEST_POSTGRES*,BONBON_SHARED_POSTGRES*,
  BONBON_PAYMENT_TEST*,BONBON_PAYMENT_STAGING*,BONBON_PAYMENT_MERCHANT* opt-in tắt.
  Chạy cả test disabled/config/schema/callback policy/connection failure bằng mock.
- Frontend: npm run build; npm run typecheck:payment; npm run build:payment-sandbox;
  npm test (31 Node tests hiện tại + regression Vitest API/storage/hook/UI).
- Bài BookingQuoteSnapshotGuardTests mới là opt-in chỉ loopback disposable, giữ skipped.
  Không chạy test localPostgres fixture trên server dùng chung; không tự tạo DB ở đây.

Kết quả lượt review: Backend Release build đạt (67 warnings hiện hữu, 0 errors),
226 passed / 0 failed / 48 skipped; in-memory lifecycle runner đạt24 cases.
Frontend typecheck:payment đạt;31 Node +23 Vitest tests passed,0 failed;
build thường, build:payment-sandbox và build:admin đều đạt. Vite còn cảnh báo
annotation SignalR và bundle trên500kB. Vitest chạy ngoài sandbox sau lỗi quyền
rename cache tạm EPERM; toàn bộ fetch trong tests vẫn là mock, không gọi dịch vụ.
JSON template parse hợp lệ, git diff --check đạt. Máy không có Docker/Caddy,
chưa validate template bằng engine hoặc chạy container/proxy. Không chạy SQL.

Workflow deploy hiện tại là production (main/manual), build admin từ frontend main,
publish image rồi dùng compose.yaml + compose.production.yaml và env file server.
Không tái sử dụng workflow đó cho sandbox. Chưa bổ sung workflow auto-deploy staging;
nhóm quản lý build image/artifact review riêng và thực hiện checklist có phê duyệt.

## 7. Danh sách file thay đổi trong lượt review

Backend (11 file, chưa stage/commit):

```text
BonBon.API/Configuration/PaymentStaging.cs
BonBon.API/Program.cs
BonBon.Tests/Payments/PaymentServiceTests.cs
BonBon.Tests/Payments/PaymentStagingSafetyTests.cs
BonBon.Tests/Integration/BookingQuoteSnapshotGuardTests.cs
database/proposals/005_booking_quote_guard.sql
deployment/compose.payment-sandbox.example.yaml
deployment/payment-sandbox.Caddyfile.example
deployment/payment-sandbox.env.example
deployment/payment-sandbox.settings.example.json
docs/PAYMENT_SANDBOX_WEB_RUNBOOK.md
```

Proposal004 được review và giữ nguyên. Không áp dụng proposal004/005 hoặc migration
nào. Local secret files tiếp tục ignored và không tracked; quét các file thay đổi
và build artifacts không khớp credential private local hoặc token/private-key pattern.
Đây không phải cam kết lịch sử Git sạch: DB/JWT credential cũ từng public vẫn cần
quản trị viên rotate/revoke theo handoff, không xử lý hoặc đưa lại giá trị ở đây.
