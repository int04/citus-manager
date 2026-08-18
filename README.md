# Citus Manager

Control-plane ASP.NET Core cho nhiều self-hosted Citus database. UI thay lệnh thủ công cho inventory, add worker, rebalance, drain và remove worker với preflight, auto-queue theo quyền, checkpoint và audit.

## Chạy bằng Docker

Yêu cầu Docker Engine/Desktop có Docker Compose. Từ thư mục chứa `compose.yaml`, chạy đúng một lệnh:

```bash
docker compose up -d
```

Mở <http://localhost:2706/Account/Setup> để tạo Admin đầu tiên. Compose tự chạy Citus Manager và PostgreSQL control DB, chờ DB healthy rồi tự apply EF Core migrations. PostgreSQL không publish port ra host hoặc LAN.

Compose chỉ tạo **control DB của Citus Manager**. Coordinator và worker Citus cần tồn tại sẵn; đăng ký chúng trong UI sau khi tạo Admin. Với Citus chạy ngay trên Docker host, dùng `host.docker.internal`; với cluster từ xa, dùng DNS/IP mà Docker host truy cập được.

Image mặc định:

```text
ghcr.io/int04/citus-manager:latest
```

Để khóa một bản phát hành, đặt tag timestamp trước khi chạy Compose:

```bash
CITUS_MANAGER_IMAGE=ghcr.io/int04/citus-manager:26.08.18.0940 docker compose up -d
```

PowerShell:

```powershell
$env:CITUS_MANAGER_IMAGE='ghcr.io/int04/citus-manager:26.08.18.0940'
docker compose up -d
```

Các lệnh vận hành:

```bash
docker compose logs -f app
docker compose pull
docker compose up -d
docker compose down
```

`postgres_data`, `app_keys`, `backup_data` và `backup_spool` là named volumes, tồn tại qua restart/recreate container. Sao lưu cả control DB, keyring và backup volumes. Mất `app_keys` sẽ làm credentials cluster đã mã hóa không thể giải mã.

> **Cảnh báo:** `docker compose down -v` xóa toàn bộ named volumes của stack, gồm control DB, keyring và backup local. Không chạy nếu chưa có backup đã kiểm chứng.

Có thể override mật khẩu control DB bằng biến `CITUS_MANAGER_DB_PASSWORD`; app và PostgreSQL nhận cùng giá trị.

## Phát hành container

Workflow **Publish container** chỉ có trigger thủ công. Vào GitHub **Actions → Publish container → Run workflow**. Push/PR không tự build hoặc publish image.

Mỗi lần chạy tạo tag theo giờ `Asia/Ho_Chi_Minh` dạng `yy.MM.dd.HHmm`, ví dụ `26.08.18.0940`, đồng thời cập nhật `latest`. Tag cùng phút đã tồn tại sẽ bị từ chối để không ghi đè bản phát hành.

Image được push tới GitHub Container Registry:

```text
ghcr.io/int04/citus-manager:<yy.MM.dd.HHmm>
ghcr.io/int04/citus-manager:latest
```

Sau lần publish đầu, owner vào package settings và đổi visibility thành **Public**. Việc chuyển source repository sang public không tự động đổi visibility package.

## Yêu cầu

- .NET SDK 10
- PostgreSQL riêng cho control DB
- Coordinator/worker đã cài PostgreSQL + Citus, network/TLS/auth sẵn
- Data Protection keyring bền vững, nằm ngoài control DB
- Prometheus/node_exporter tùy chọn

App không provision VM/container, không sửa firewall/`pg_hba.conf`, không xóa volume, không thay thế backup/PITR/HA.

## Cấu hình

Không commit secrets. Dùng environment variables hoặc secret manager:

```powershell
$env:ConnectionStrings__ControlDatabase='Host=control-db;Port=5432;Database=citus_manager;Username=citus_manager;Password=<SECRET>;SSL Mode=VerifyFull'
$env:Security__DataProtectionKeyPath='D:\protected\citus-manager-keys'
$env:Notifications__WebhookUrl='https://hooks.example/internal/...'
$env:Notifications__Smtp__Host='smtp.example'
$env:Notifications__Smtp__Username='<USER>'
$env:Notifications__Smtp__Password='<SECRET>'
```

Production: mount keyring trên encrypted persistent storage; backup/restore key cùng control DB. Mất key → không giải mã cluster credentials.

## Khởi tạo

```powershell
dotnet restore
dotnet ef database update
dotnet run
```

Mở `/Account/Setup` và tạo Admin đầu tiên. User có quyền tạo operation sẽ tự đưa operation đó vào hàng đợi; không cần Admin thứ hai.

Development có `Database:AutoCreateSchema=true`, app tự chạy migrations. Production mặc định tắt; apply migration trong controlled deployment.

## Roles

- `Viewer`: dashboard, topology, database explorer/SQL, metrics, activity, alerts.
- `Operator`: thêm profile, tạo operation plan, acknowledge alert, request cancel.
- `Admin`: quản lý user/profile và audit; các operation được phép tạo sẽ tự vào hàng đợi.

## Database explorer

Mở từ **Dữ liệu bảng** trong cluster Details. Coordinator browser đọc logical table nên thấy dữ liệu toàn cluster. Nút **Dữ liệu node** kết nối trực tiếp node topology bằng credential cluster và chỉ đọc physical shard placements trên node đó.

SQL console chỉ chạy trên coordinator, cho mọi user đã đăng nhập và không giới hạn loại statement PostgreSQL. Mỗi lần chạy phải xác nhận; mặc định timeout 60 giây, tối đa 1.000 rows/result set và audit chỉ lưu SHA-256/metadata, không lưu SQL plaintext. Quyền thực tế vẫn do database role trong cluster profile quyết định.

## Safety invariants

- Capability scan theo database/version/function signature; feature thiếu → chặn.
- Mọi topology mutation: immutable plan → auto-queue theo quyền → live preflight → runner.
- Một impact operation/cluster nhờ PostgreSQL advisory lock.
- Add worker không tự rebalance.
- Drain cancel không trả shards đã chuyển.
- Remove worker luôn kiểm tra `placements_left = 0`; khác 0 → tuyệt đối chặn.
- Worker mất và còn unique shard → `RecoveryRequired`, không giả vờ remove có thể phục hồi data.
- Password/token mã hóa; không trả qua API/log/audit. Activity không hiển thị SQL text/parameters.

## Monitoring

SQL collector mặc định 60 giây, raw retention 30 ngày. Theo dõi node active, metadata sync, placements, shard bytes, table count. Prometheus tùy chọn bổ sung target/CPU/RAM/filesystem aggregate. Alert xuất hiện in-app; webhook/SMTP retry tối đa năm lần.

Pairwise `citus_check_cluster_node_health()` không poll liên tục vì có thể mở nhiều connections; chạy qua runbook/diagnostic có kiểm soát.

## Kiểm tra

```powershell
dotnet build
dotnet test
dotnet list package --vulnerable --include-transitive
```

[CitusManager.http](CitusManager.http) chứa mẫu request cho mọi API chính. OpenAPI development: `/openapi/v1.json`.

## Phạm vi tiếp theo

Topology lifecycle, monitoring và database explorer đã hoạt động. Schema/table wizard nâng cao (distributed/reference table, shard-count, colocation, tenant/schema move) phải dùng cùng operation engine và chỉ bật sau staging rehearsal theo Citus version/workload thực tế.
