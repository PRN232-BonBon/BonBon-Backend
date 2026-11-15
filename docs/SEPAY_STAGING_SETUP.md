# BonBon Backend — SePay Sandbox staging riêng

## Trạng thái và phạm vi

Ngày kiểm tra: 16/11/2026. Nhánh `review/payment-sandbox-web` có thay đổi chưa commit được giữ nguyên.
Chỉ sửa Backend/cấu hình mẫu. Không sửa Frontend, không SSH/deploy/push, không mở tunnel,
không bật SePay, không tạo Payment, không kết nối hoặc chạy SQL trên database hiện có.

| Hostname | Trạng thái |
|---|---|
| `bonbon-api.kitsuracloud.com` | Production do nhóm cung cấp; DNS phân giải được. Không đổi hoặc thử ghi |
| `bonbon-api-staging.kitsuracloud.com` | Đề xuất của nhóm, chưa tạo/xác minh; DNS chưa phân giải từ máy kiểm tra |
| `bonbon-staging.kitsuracloud.com` | Đề xuất Frontend, chưa tạo/xác minh; DNS chưa phân giải từ máy kiểm tra |

Các hostname đề xuất không được nhập vào cấu hình đang chạy. Các mẫu để trống PublicOrigin,
FrontendOrigin, ReturnUrl, FrontendResultUrl, credential và readiness. Frontend staging chưa có:
CORS không cấp origin nào và `SePay.Enabled=false`. Không dùng Frontend production thay thế.

## Những gì repository đang triển khai

- `compose.yaml`: project `bonbon`, PostgreSQL/API production, env `.env`, DB mặc định `bonbon`,
  hai init SQL cho cluster mới và cổng API host 8080. Không dùng file này cho SePay staging.
- `compose.production.yaml`: override image bất biến và mount admin portal.
- `Dockerfile`: .NET SDK/runtime 8, publish API, runtime non-root, curl healthcheck, diagnostics tắt.
- `.github/workflows/deploy.yml`: push main hoặc workflow_dispatch → test, build/push GHCR,
  runner self-hosted production → Compose pull/up API và health check; đồng thời build admin từ Frontend main.
  **Không chạy workflow này để deploy staging**. Manual dispatch hiện không có guard branch riêng cho job deploy;
  người quản trị phải xem đây là workflow production kể cả khi chọn một ref khác. Không sửa workflow production trong bước này.
- Repository có Caddy template; cấu hình edge thực tế trên server chưa được truy cập/xác minh.
  Không suy ra production chắc chắn đang dùng đúng template.
- Secret hiện có được Git/Docker ignore và Web SDK loại khỏi publish. File local không được copy vào image.
  REST SePay merchant đã xác thực HTTP 200 ở lần kiểm tra local trước; điều này chưa chứng minh staging được cấu hình.

## Topology đã chuẩn bị

Dùng **VM/host staging riêng**, không dùng server/Compose project/network/volume production.
`deployment/compose.sepay-staging.example.yaml` là project độc lập `bonbon-sepay-staging`:

```text
Internet HTTPS → Caddy edge → API PaymentStaging → PostgreSQL staging riêng
                    │          │
             chỉ 80/443    outbound HTTPS → pgapi-sandbox.sepay.vn
```

Không có Frontend service hoặc bản build Frontend trong topology này. Frontend HTTPS origin sau này có thể nằm host khác.
Không publish API/PostgreSQL, không expose Tailscale. Data network Docker là internal; proxy không nằm trong data network.
Chỉ edge publish 80/443 trên **host mới**, tuyệt đối không khởi chạy cạnh production edge cùng host/cổng.

Hai subnet và IP trong template là **địa chỉ mạng thiết kế**, không phải thông tin hạ tầng hiện có:
edge `172.30.91.0/24` (Caddy .2, API .3), data `172.30.92.0/24` (PostgreSQL .2, API .3).
Admin phải kiểm tra xung đột route/network trước khi chấp thuận; nếu đổi, cập nhật đồng bộ template,
TrustedProxyAddresses và ExpectedDatabaseAddress. Không dùng network external của production.

Image API/Caddy/PostgreSQL phải do admin review và pin digest. DB guard hiện pin PostgreSQL **16.15** bằng
`server_version_num=160015`, không phụ thuộc suffix packaging của vendor. Nâng patch cần review guard/image cùng nhau.
Các volume dùng tên staging riêng và `external: true`: admin xác minh nhãn/quyền sở hữu trước khi tạo/chọn.
Không tự dùng volume có sẵn hoặc xóa volume để khắc phục lỗi startup.

## Profile và database identity guard

Môi trường ASP.NET Core: `PaymentStaging`; chọn `PaymentStaging:DatabaseProfile=SePayDedicated`.
Profile cũ và PaymentLocal không thay đổi target hay được nới guard.

| Thuộc tính | Bắt buộc |
|---|---|
| Database | `bonbon_payment_staging` |
| Runtime role | `bonbon_payment_staging_runner`, LOGIN, CONNECTION LIMIT 4, không superuser/CREATEDB/CREATEROLE/BYPASSRLS/REPLICATION |
| Owner | `bonbon_payment_staging_owner`, NOLOGIN; runtime không sở hữu DB/schema và không được membership role khác |
| Server | cluster riêng; host pin độc lập, IP data-container pin, port 5432, PostgreSQL 16.15 |
| DB comment | `bonbon-sepay-staging:v1:<UUID riêng do admin tạo>`; phải khớp chính xác secret config |
| Pool | max 2, min 0; reset bật, không multiplex, không log parameter/error detail |
| Timeout | Connection 1–10 giây; command 1–15 giây |
| JWT | issuer `BonBon.SePayStaging`, audience `BonBon.SePayStaging.Web`, key mới >=64 ký tự |

Physical connection initializer kiểm tra DB/user/session_user/owner/marker/server address/version/role/privilege.
Runtime không có CREATE DB/schema, không CONNECT database postgres; cluster không chứa database nghiệp vụ khác.
Startup/health còn kiểm tra marker 004/006/007, quote guard 005 đang enabled, snapshot fields,
12 unique indexes theo provider, 2 indexes cross-provider, CHECK SePay, audit FK và control stage ready.
Sai target/schema/quyền sẽ từ chối startup hoặc readiness; không migrate tự động.

## DBA chuẩn bị cluster và schema — CHƯA ĐƯỢC THỰC THI

Các lệnh dưới đây là hướng dẫn cho quản trị viên **sau khi host/DB và triển khai được phê duyệt**.
Không chạy trên `bonbon`, `bonbon_payment_local`, DB nhóm hoặc connection string production.

1. Xác minh VM/Docker context/project, volume mới, cluster identity, current_database/current_user,
   socket/server address và phiên bản. Cluster ban đầu chỉ có postgres/template DB.
2. Khởi động riêng service PostgreSQL trên host staging sau phê duyệt, chưa khởi động API/edge.
   Không mount init SQL/migration tự chạy. Superuser cluster chỉ dành DBA, không cấp cho API.
3. Tạo owner NOLOGIN và runtime LOGIN giới hạn quyền. Đặt password runtime qua `psql \password`
   hoặc secret manager; không đặt password trong command line, SQL file, `.env` public hay shell history.

```sql
-- REVIEW ONLY: new isolated staging cluster, administrator session.
CREATE ROLE bonbon_payment_staging_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE bonbon_payment_staging_runner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 4;
CREATE DATABASE bonbon_payment_staging OWNER bonbon_payment_staging_owner;
REVOKE CONNECT, TEMPORARY ON DATABASE bonbon_payment_staging FROM PUBLIC;
GRANT CONNECT ON DATABASE bonbon_payment_staging TO bonbon_payment_staging_runner;
-- Only on this new cluster: prevent runtime access to the maintenance/template DBs.
REVOKE CONNECT, TEMPORARY ON DATABASE postgres FROM PUBLIC;
REVOKE CONNECT, TEMPORARY ON DATABASE template1 FROM PUBLIC;
-- Add the independently generated matching marker as COMMENT ON DATABASE, in the private DBA session.
```

4. Kết nối DB staging mới, xác minh lại identity trước DDL. Đặt owner/schema/function ownership nhất quán
   bằng owner role trong phiên DBA; không cho runtime role chạy DDL.
5. Backup trước các bước thay đổi, kiểm tra archive inventory và diễn tập restore trên bản clone staging độc lập
   được phê duyệt. Không dùng bản dump có dữ liệu khách hàng thật.
6. Chọn **baseline schema-only đã review**. Không clone dữ liệu production và không chạy 001/002/003 theo số máy móc.
   Baseline đã kiểm thử local trước dùng 001 hiện tại (đã có flexible duration), 003_max_daily_rental,
   003_vehicle_tracking, 004_minimal_vehicle_details, 006_owner_vehicle_registration,
   007_remove_vehicle_registration_document, 008_admin_vehicle_review. Review migration tracking bổ sung nếu scope cần.
   Không chạy lại 002_flexible_booking_duration khi 001 đã chứa cấu trúc tương đương; không chạy demo seed 005.
   Main mới có `010_optional_password_hash.sql` phục vụ Auth/Google: giữ nguyên file của nhóm và review cùng baseline.
   Migration này có UPDATE để chuẩn hóa hash không phải BCrypt, nên chỉ áp dụng có backup trên cluster staging mới
   trước khi tạo tài khoản synthetic; không chạy tự động hoặc mang sang production trong bước tích hợp này.
   Đây là manifest cần DBA đối chiếu schema/function, không phải script bootstrap tự động.

| Proposal Payment | Điều kiện trước khi áp dụng | Kiểm tra sau |
|---|---|---|
| `004_vnpay_sandbox.sql` | payment baseline tương thích, chưa có marker/cột proposal | marker v1, notification, cột idempotency/expiry/audit, 4 unique indexes |
| `005_booking_quote_guard.sql` | Booking snapshot fields từ schema owner-registration, trigger guard tồn tại/enabled | khóa participants/price/time/discount/terms sau decision |
| `006_momo_sandbox.sql` | 004 đã review, chưa có MoMo marker | audit provider columns, provider CHECK, 4 indexes và 2 cross-provider indexes; MoMo vẫn tắt |
| `007_sepay_sandbox.sql` | 004/005/006 đúng baseline, không có SePay drift | marker v1, UUID storage, evidence/rejected tables, SePay CHECK/FK/index và 3-provider predicates |

007 hiện phụ thuộc phần provider/audit schema của 006, không có nghĩa được bật MoMo. Không bỏ prerequisite guard.
Các proposal có transaction/timeout và từ chối rerun không phù hợp. Nếu marker đã tồn tại, inspect schema/checksum,
không xóa marker hoặc DROP dữ liệu để chạy lại. Lỗi trước COMMIT phải rollback; sau có receipt ưu tiên roll forward.

7. Sau schema review, DBA tạo control metadata trên DB mới (không phải migration nghiệp vụ):

```sql
CREATE TABLE public.payment_staging_control (
  singleton integer PRIMARY KEY CHECK(singleton=1),
  stage text NOT NULL CHECK(stage IN('preparing','ready'))
);
ALTER TABLE public.payment_staging_control OWNER TO bonbon_payment_staging_owner;
INSERT INTO public.payment_staging_control VALUES(1,'preparing');
REVOKE ALL ON SCHEMA bonbon,public FROM PUBLIC;
GRANT USAGE ON SCHEMA bonbon,public TO bonbon_payment_staging_runner;
GRANT SELECT ON ALL TABLES IN SCHEMA bonbon TO bonbon_payment_staging_runner;
GRANT SELECT ON public.payment_staging_control TO bonbon_payment_staging_runner;
GRANT INSERT,UPDATE ON bonbon.account,bonbon.account_role,bonbon.refresh_token,bonbon.vehicle,bonbon.booking,bonbon.payment
  TO bonbon_payment_staging_runner;
GRANT DELETE ON bonbon.refresh_token TO bonbon_payment_staging_runner;
GRANT INSERT ON bonbon.payment_notification,bonbon.sepay_payment_evidence,bonbon.sepay_rejected_notification
  TO bonbon_payment_staging_runner;
-- Review function inventory/SECURITY DEFINER/search_path first; grant only the reviewed baseline functions needed by API.
-- Do not grant runtime role membership in owner or cluster-admin roles.
```

Với baseline hiện tại cần EXECUTE cho `has_active_role`/`create_booking` và các helper được gọi;
DBA tra đúng function signatures bằng metadata trước GRANT, không đoán kiểu tham số hoặc cấp quyền vào function lạ.
Không cấp DDL, DELETE financial/audit, quyền schema CREATE hoặc default grant quá rộng.
Nếu quote/audit trigger cần thêm quyền, review phạm vi cụ thể, không chuyển runtime thành owner/superuser.

8. Kiểm tra index definitions/predicates, convalidated, FK, function bodies, trigger linkage/enabled, marker,
   runtime grants và schema-only diff; sau đó mới đặt control stage='ready' trong DB staging.
9. Chạy `database/preflight/sepay_staging_readonly.sql` bằng runtime role với
   `expected_marker` và `expected_db_address` do admin đã xác minh. Script BEGIN READ ONLY,
   timeout 10 giây; không CREATE/ALTER/INSERT/UPDATE/DELETE và dừng nếu không khớp.
   Trong container, dùng địa chỉ data-container `172.30.92.2` (hoặc giá trị đã duyệt), không `127.0.0.1`,
   để so sánh inet_server_addr đúng. Password nhập bằng prompt `-W`/secret manager, không tham số CLI.
   Script chưa được chạy vì cluster staging chưa tồn tại.

## Secrets và cấu hình ASP.NET Core

Các mẫu:

- `deployment/sepay-staging.settings.example.json`: DB/JWT/host/marker/readiness, không credential thật.
- `deployment/sepay-staging.merchant.example.json`: root SePay duy nhất, endpoints Sandbox chính thức.
- `deployment/sepay-staging.env.example`: chỉ image digest, hostname đã duyệt, tester CIDR và đường dẫn file.

Admin tạo file private **ngoài repository**, đặt quyền đọc đúng non-root UID của API image (kiểm tra UID image thực tế).
Compose secrets dạng file bind mount không phải vault mã hóa; cần bảo vệ file host/backup, không dựa vào `uid/gid`
trong Compose để sửa ownership của file bind-mounted. API chỉ nhận runtime password; không nhận cluster-admin secret.
Không copy file secret local hiện có lên server một cách tự động hoặc dùng JWT/password production.

`BONBON_PAYMENT_STAGING_CONFIG=/run/secrets/staging_config`,
`BONBON_SEPAY_CONFIG=/run/secrets/sepay_config`. File SePay chỉ được có root SePay,
không override DB/JWT. Environment variable có ưu tiên sau file: admin kiểm tra các override ngoài ý muốn,
chỉ báo SET/MISSING/INVALID, không in docker inspect env hoặc `cat` file secret.
Compose ép profile `SePayDedicated` và giữ VNPay/MoMo/simulator tắt bằng biến không bí mật;
copy nhầm config staging cũ sẽ bị database identity guard từ chối, không âm thầm dùng DB test của nhóm.

Connection string chỉ để trong file private:
Host=sepay-postgres;Port=5432;Database=bonbon_payment_staging;Username=bonbon_payment_staging_runner;
Password lấy từ secret manager;Pooling=true;Maximum Pool Size=2;Minimum Pool Size=0;Timeout=5;Command Timeout=10.
Đây là mạng data nội bộ của host staging. Nếu chuyển PostgreSQL ra host khác, cần review TLS VerifyFull,
pin server và topology riêng; không đổi Host để trỏ vào server nhóm.

| Key | Quy tắc |
|---|---|
| PaymentStaging:PublicOrigin | HTTPS Backend staging thật, root origin; template đang để trống |
| PaymentStaging:FrontendOrigin | HTTPS Frontend staging được tin cậy; **để trống hiện tại** |
| AllowedHosts | Backend staging host và tùy chọn localhost/127.0.0.1; không wildcard/production host |
| SePay:PublicOrigin | Khớp Backend origin đã xác minh |
| SePay:ReturnUrl | Backend origin + `/api/payments/sepay/return` |
| SePay:FrontendResultUrl | Chính xác FrontendOrigin + `/payment/result`; không query/fragment/userinfo; đang để trống |
| SePay:IpnSecretKey | Key riêng >=24 ký tự, khác MerchantSecretKey, khớp tab IPN |
| SePay:MerchantSecretKey | Test-mode key `spsk_test_...`, không key live |
| SePay:SandboxCheckoutUrl | `https://pay-sandbox.sepay.vn/v1/checkout/init` |
| SePay:SandboxApiBaseUrl | `https://pgapi-sandbox.sepay.vn` |
| SePay:Enabled | false cho đến khi mọi điều kiện đủ và admin được phê duyệt bật |

FrontendResultUrl có HTTPS origin khác Backend nhưng chỉ từ cấu hình tin cậy, đối chiếu FrontendOrigin.
Không lấy redirect URL từ query/request. Return chỉ chuyển paymentId/provider, không chuyển kết quả thành công.
Frontend sau này GET Payment bằng JWT; chỉ hiển thị thành công khi Succeeded và requiresReconciliation=false.

## DNS, TLS, proxy và CORS

1. Admin xác nhận quyền quản lý zone, cấp host staging riêng và tạo A/AAAA cho hostname **mới**.
   Không sửa record/domain production hoặc wildcard route. Frontend staging hostname chỉ tạo sau khi nhóm chốt.
2. Kiểm tra public DNS từ bên ngoài, IP đúng host staging, IPv6 cũng tới đúng host hoặc không công bố AAAA.
3. Trên host staging, admin kiểm tra 80/443 chưa thuộc dịch vụ khác, cấu hình ACL/ACME cần thiết sau phê duyệt.
   Không thay firewall hoặc certificate production. Caddy cấp TLS public hợp lệ, không self-signed/`tls_insecure_skip_verify`.
4. `deployment/sepay-staging.Caddyfile.example` chỉ cho POST IPN và GET Return public.
   Không JWT/basic-auth/IP tester restriction cho hai callback; SePay phải truy cập trực tiếp được.
5. IPN body tối đa 32768 bytes ở Caddy và ASP.NET Core; POST JSON, response status được giữ nguyên.
   IPN chỉ trả 200 khi handler đã áp dụng/deduplicate/ghi nhận cần đối soát; không dùng respond 200 tại proxy che lỗi.
6. API khác bị whitelist theo path/method và tester CIDR /32 hoặc /128; vẫn bắt buộc JWT/role/ownership ở Backend.
   OPTIONS được phép cho các API cần thiết; CORS chỉ dùng FrontendOrigin mới, không kế thừa production origins.
   Khi origin rỗng, không cấp quyền CORS. Server-to-server SePay không phụ thuộc CORS.
7. `/health` chỉ dùng nội bộ Docker; `/swagger`, admin, tracking, simulator và route khác trả 404 ở edge.
   Không mount admin assets/Frontend production vào API. Template không có Frontend SPA fallback vì không phục vụ Frontend.
8. Proxy chuyển X-Forwarded-Proto/For, không tin X-Forwarded-Host tùy ý. API chỉ tin exact proxy IP đã pin,
   ForwardLimit=1 và header symmetry; không KnownNetworks wildcard. Direct edge template không áp dụng nguyên xi cho CDN/tunnel.
9. Không log request body, query đã ký, Authorization hoặc X-Secret-Key. Caddy access log discard;
   Docker logs bounded, API warning-level. Có thể bổ sung metrics/status counters đã redaction sau review.

## Deploy sau khi có phê duyệt — không tự chạy các lệnh này

Không dùng workflow production hoặc ghép file Compose production. Admin chuẩn bị riêng image/digest/secret/network/volume.
Lệnh mẫu (đường dẫn env file là private/non-secret deployment coordinates trên staging):

```bash
# After explicit staging-host deployment approval only:
docker compose --project-name bonbon-sepay-staging --env-file /private/staging/deploy.env \
  -f deployment/compose.sepay-staging.example.yaml config --quiet
# Start database only, perform approved DBA provisioning/preflight before starting API/edge.
docker compose --project-name bonbon-sepay-staging --env-file /private/staging/deploy.env \
  -f deployment/compose.sepay-staging.example.yaml up -d sepay-postgres
# After dedicated DB/schema and private configuration pass preflight:
docker compose --project-name bonbon-sepay-staging --env-file /private/staging/deploy.env \
  -f deployment/compose.sepay-staging.example.yaml up -d api
# Validate Caddy syntax with the pinned staging image before publishing edge.
docker compose --project-name bonbon-sepay-staging --env-file /private/staging/deploy.env \
  -f deployment/compose.sepay-staging.example.yaml run --rm --no-deps edge \
  caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
# Publish edge only after DNS/TLS and explicit approval:
docker compose --project-name bonbon-sepay-staging --env-file /private/staging/deploy.env \
  -f deployment/compose.sepay-staging.example.yaml up -d edge
```

Không `compose down -v`, `prune`, force recreation hoặc sửa file server production. Nếu volume/network có sẵn,
xác minh ownership/labels trước, không ghi đè. API guard chặn mọi target sai, kể cả khi SePay đang tắt.
Kiểm tra health trong API container; source `/health` hiển thị đúng bonbon_payment_staging, databaseVerified,
paymentEnabled=false và sepayEnabled=false. Không public health để lộ metadata hạ tầng.

## Kiểm tra callback an toàn trước E2E

Hiện chưa có DNS/TLS staging, nên **chưa thể gửi request callback tới staging thật**.
Đã kiểm tra bằng TestServer in-memory: public POST IPN tới handler thật trả 503 khi disabled,
401 khi enabled trong test mà thiếu key; không JWT challenge. Return dùng fixed frontend URL;
open-redirect query bị bỏ qua. CORS allowlist và trusted/untrusted forwarded headers có regression tests.
Repo/gateway trong HTTP tests là fake; không tạo Payment/Booking/audit trong PostgreSQL và không phải bằng chứng public delivery.

Khi DNS/TLS và deployment đã được phê duyệt, admin/người kiểm thử có thể chạy:

```powershell
# Use the approved actual origin, not a proposed/nonexistent hostname.
powershell -File tools/Test-SePayStagingCallback.ps1 -PublicOrigin '<verified HTTPS Backend staging origin>'
```

Script giữ TLS validation, không theo redirect, không key/JWT/invoice/paymentId và không success callback.
POST `CONNECTIVITY_PROBE` phải tới đúng endpoint marker `ipn-v1`: disabled trả 503, enabled thiếu key trả 401.
GET Return phải có marker `return-v1`: thiếu config trả 503 hoặc config đủ redirect 302 cố định.
Đây là bằng chứng **routing/TLS từ máy gọi**, không chứng minh SePay đã gọi tới hoặc thanh toán thành công.
Khi enabled, negative auth probe có thể ghi một rejected-audit hash đã deduplicate; không đổi Payment/Booking/receipt.
403 ở callback có thể do tester ACL/WAF; 404 do route sai; 413 do body; redirect đăng nhập do edge auth không phù hợp;
503 không có endpoint marker có thể là proxy/API chưa sẵn sàng. Không khắc phục bằng response 200 giả.

## Dashboard SePay Test Mode

1. Đăng nhập merchant, chọn đúng Cổng thanh toán/Test Mode (không Live).
2. Cấu hình → IPN: URL = Backend staging HTTPS origin thật + `/api/payments/sepay/ipn`.
3. Auth Type = `SECRET_KEY`; đặt **IPN key riêng** giống SePay:IpnSecretKey trong secret staging.
   MerchantSecretKey dùng ký checkout/REST, không dùng thay IPN key. Không gửi key qua chat/screenshot/log.
4. Lưu cấu hình; admin xác nhận URL/Auth Type/key đã khớp. REST list-order API không cung cấp bằng chứng tab IPN đã cấu hình.
5. Return URL được Backend đưa vào success/error/cancel form fields, đều cùng endpoint Return cố định.
   Khi Frontend origin chưa có, giữ FrontendResultUrl rỗng và Enabled=false.
6. Nếu Dashboard có test delivery, chỉ dùng bài negative connectivity không đại diện thanh toán hoặc xác nhận với SePay hỗ trợ.
   Không gửi ORDER_PAID giả cho invoice thật để tuyên bố E2E đã đạt.

Tài liệu: [IPN/key riêng](https://developer.sepay.vn/vi/cong-thanh-toan/IPN),
[Sandbox endpoints](https://developer.sepay.vn/vi/cong-thanh-toan/sandbox),
[Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/),
[ASP.NET forwarded headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-8.0).

## Readiness và trách nhiệm

| Flag | Chỉ đặt true khi |
|---|---|
| SePayMerchantVerified | Chính instance/credential staging xác thực REST Sandbox qua TLS, có HTTP/contract evidence |
| SePayCallbacksVerified | DNS/TLS/proxy route public và negative auth probe đạt, Dashboard Test Mode IPN URL/Auth Type được xác nhận; ghi rõ chưa phải payment E2E |
| SePayReconciliationPlanVerified | Nhóm duyệt cách query/manual-review tiền đến muộn/VOID/timeout, người chịu trách nhiệm và cách dừng test |

Cả ba hiện false; không tự đặt true từ kết quả unit test hoặc merchant local lần trước.
Callback readiness là kiểm tra hạ tầng/auth trước khi cho phép giao dịch, không đồng nghĩa đã có IPN payment thật.
SePay.Enabled còn yêu cầu credential/IPN key, hai origin/Return đầy đủ, schema/physical DB guard pass.
Nếu chỉ Backend hostname có mà chưa có Frontend origin, vẫn không bật. Không bỏ guard để thử thành công.

| Việc | Người thực hiện |
|---|---|
| Code, templates, preflight đọc, regression và tài liệu | Codex: đã chuẩn bị, không deploy |
| VM/cluster mới, DNS A/AAAA, ACME/TLS, tester ACL, image/network/volume | Quản trị hạ tầng, sau phê duyệt |
| Role/marker/backup/schema-only baseline, 004–007, grants, preflight | DBA trên cluster staging mới |
| Secret manager và cấu hình file/mount | Quản trị staging/merchant, không đưa vào Git |
| Dashboard IPN SECRET_KEY/Test Mode | Người có quyền merchant SePay |
| Hostname/ứng dụng Frontend staging và form POST SePay | Nhóm Frontend/hạ tầng; không làm trong bước Backend này |
| Phê duyệt publish edge/bật Sandbox/thực hiện giao dịch | Người dùng và nhóm quản trị |

## Rollback và checklist E2E

- Startup/config/schema fail: dừng API/edge staging riêng, giữ DB/volume/backup để điều tra; không chuyển sang DB production.
- Cấu hình sai: phục hồi file private phiên bản đã duyệt và image digest staging trước, restart **staging** sau phê duyệt.
  Không rollback SQL bằng cách drop Payment/audit. Giữ merchant/key cũ an toàn khi còn attempt chưa đối soát.
- Sau khi đã có callback/receipt: dừng tạo checkout mới; cần tiếp tục nhận/reconcile callback bằng cấu hình được duyệt.
  Hiện Enabled là flag chung cho create/IPN, vì vậy tắt flag sẽ trả 503 IPN và gây retry: phải có kế hoạch drain,
  query/reconciliation thủ công, không đơn giản tắt rồi bỏ callback. Không tự sửa Succeeded/Confirmed.
- Restore archive chỉ trên clone/staging được duyệt, sau khi lưu trạng thái/audit hiện tại; không restore đè production.

Trước một giao dịch do người dùng tự thao tác:

- [ ] Host Backend/Frontend HTTPS thật đã cấp và được xác minh; TLS hợp lệ, không open redirect.
- [ ] Cluster mới đúng bonbon_payment_staging; không production/PaymentLocal, snapshot/backup/restore drill đạt.
- [ ] 004/005/006/007/schema/grants/preflight pass, runtime không có DDL hoặc quyền cluster khác.
- [ ] Merchant/IPN keys khác nhau, Sandbox endpoints đúng; không có secret trong Git/image/log.
- [ ] IPN public POST, Return public GET; tester APIs/JWT/CORS/ForwardedHeaders/body limit đúng.
- [ ] Dashboard Test Mode IPN đã lưu; ba readiness flag có bằng chứng và được người có quyền xác nhận.
- [ ] Frontend đã tích hợp ordered form POST; GET status bằng JWT, không dựa query Return để báo thành công.
- [ ] Tài khoản/xe/Booking synthetic mới, giá DB, pickup tương lai, Booking được Owner duyệt đúng luồng.
- [ ] Người dùng duyệt publish/bật riêng sandbox và tự thao tác; không dùng tiền/thẻ thật hoặc simulator.
- [ ] Đối chiếu Payment Succeeded + requiresReconciliation=false, Booking Confirmed, transaction UUID và đúng một receipt/evidence.
- [ ] IPN đến muộn/timeout/reload/duplicate không tạo attempt hoặc receipt mới; có operator xử lý đối soát.

Giới hạn còn tồn tại: chưa có Frontend SePay; chưa có staging DNS/TLS/DB; chưa diễn tập public callback hoặc E2E thật.
Chưa có reconciliation worker/backoff, Booking TTL giải phóng lịch, refund/void/payout/Wallet và quy trình xử lý tiền đến muộn tự động.
Transaction_date hiện đọc UTC+07:00 theo lựa chọn tích hợp; cần xác nhận timezone/UUID response thực tế với SePay trước E2E.
Checkout expiry nội bộ không bảo đảm provider dừng nhận tiền. Không dùng các flag để che các giới hạn này.

Máy kiểm tra hiện không có Docker/Caddy: chưa chạy `docker compose config`/`caddy validate` hoặc start container.
Admin phải thực hiện các validation đó với image pinned trước deploy; mọi lệnh triển khai trong tài liệu vẫn chưa được chạy.

## Kết quả kiểm tra code và danh sách file của bước này

- Build Release vào artifacts riêng: thành công, 0 errors; không ghi đè binary Backend local đang chạy.
- Unit/regression/TestServer: **354 passed, 0 failed, 1 skipped**; PostgreSQL integration bị loại khỏi lượt chạy.
- HTTP in-memory kiểm tra anonymous IPN 503/401, Return fixed redirect, JWT checkout,
  CORS đúng/sai origin và forwarded headers từ proxy tin cậy/không tin cậy.
- JSON templates parse thành công; PowerShell callback probe parse không lỗi; `git diff --check` đạt.
- Quét 222 file source/config/docs không thấy giá trị merchant credential local. Không in giá trị bí mật.
- 146 file Frontend đối chiếu checksum: không đổi. Không stage/commit/push, không sửa production Compose/workflow/Dockerfile.
- Không chạy migration, không tạo DB/role/network/volume/container/DNS hoặc Payment attempt.
  Không gọi callback tới hostname đề xuất, không xác nhận public TLS/IPN hoặc E2E đã hoạt động.

Tạo mới:

```text
BonBon.API/Configuration/SePayStaging.cs
BonBon.Tests/Payments/SePayStagingTests.cs
deployment/compose.sepay-staging.example.yaml
deployment/sepay-staging.Caddyfile.example
deployment/sepay-staging.env.example
deployment/sepay-staging.settings.example.json
deployment/sepay-staging.merchant.example.json
database/preflight/sepay_staging_readonly.sql
tools/Test-SePayStagingCallback.ps1
docs/SEPAY_STAGING_SETUP.md
```

Sửa trong các file đã có:

```text
BonBon.API/Configuration/PaymentStaging.cs
BonBon.API/Program.cs
BonBon.API/Controllers/SePayController.cs
docs/SEPAY_BACKEND_INTEGRATION.md
```

Các thay đổi chưa commit trước bước này được giữ nguyên. Database guard mới chưa được kiểm thử với
cluster staging thật vì chưa có hạ tầng; SQL/Caddy/Compose còn cần DBA/admin validate trên host cô lập sau phê duyệt.
