# Tenhou CSV Reader (Windows, Read-Only)

WPF Windows 應用程式，專門讀取超大 CSV（可到 5GB 等級），並提供分頁瀏覽、篩選、連結預覽與複製功能。

## 功能重點

- Read-only 讀取，不改動原始 CSV
- 大檔優化：分頁載入 + 背景索引 + DataGrid 虛擬化
- 支援拖拉 `.csv` 檔到視窗直接開啟
- Filter 支援：`=`, `!=`, `>`, `>=`, `<`, `<=`
- 複製功能：
  - `Copy Cell`
  - `Copy Row As CSV`（維持 CSV 格式，不是 tab）
  - `Ctrl + C` 也使用 CSV 格式
- 連結互動：
  - `Ctrl + Click` 外部瀏覽器開啟
  - `Double Click` 在右側分割面板內嵌開啟
- 右側瀏覽面板使用 **Edge WebView2**（不是 IE）

## 開發執行

1. 進入專案：
   - `cd TenhouCsvReader`
2. 建置：
   - `dotnet build`
3. 執行：
   - `dotnet run`
4. 或從 repo 根目錄直接執行：
   - `dotnet run --project TenhouCsvReader/TenhouCsvReader.csproj`

## Filter 範例

- `prediction = 1, actual = 0, probability > 0.7`
- 欄位名稱大小寫不敏感
- 支援唯一前綴（例如 `pred = 1` 會對到 `prediction`）

## 圖示

- 來源：`images/cover.png`
- 產物：`TenhouCsvReader/Assets/app.ico`
- 已做多尺寸（16~256）並套用到 EXE 與視窗圖示。

## Release 交付形式

本專案支援兩種發佈情境：

1. MSI 安裝版（建議一般使用者）
2. Portable ZIP 免安裝版（解壓後直接執行）

### 一鍵產生 Release

在 repo 根目錄執行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```

或指定新版 MSI 版本（舊版會自動升級）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -ProductVersion 1.0.2
```

輸出位置：

- `artifacts/TenhouCsvReader-setup-x64.msi`
- `artifacts/TenhouCsvReader-portable-win-x64.zip`
- `artifacts/SHA256SUMS.txt`

### MSI 安裝版行為

- 安裝到 `Program Files`（x64）
- 安裝流程可選擇安裝路徑
- 安裝流程可輸入 Start Menu 名稱
- 安裝流程可勾選是否建立桌面捷徑
- 建立開始選單項目與解除安裝捷徑（名稱依安裝時輸入）
- 安裝完成頁可勾選「立即開啟程式」
- 使用固定 `UpgradeCode` + `MajorUpgrade`：新版本 `ProductVersion` 較高時會自動偵測舊版並執行升級
- 會註冊到 Windows 已安裝應用程式清單，可在：
  - 設定 > 應用程式 > 已安裝應用程式
  - 直接按「解除安裝」

### Portable ZIP 行為

- 解壓縮後直接執行 `TenhouCsvReader.exe`
- 不寫入安裝註冊資訊（不會出現在已安裝應用程式清單）

## WebView2 注意事項

- 右側內嵌瀏覽需要 Microsoft Edge WebView2 Runtime。
- WebView2 使用者資料夾會寫到 `%LOCALAPPDATA%\\TenhouCsvReader\\WebView2`，避免安裝到 `Program Files` 時發生權限問題。
- 多數 Windows 10/11 已內建；若目標機器缺少，安裝後即可使用內嵌瀏覽。
