# Tenhou CSV Reader (Windows, Read-Only)

這個專案是一個 WPF Windows 應用程式，用來讀取超大 CSV（約 5GB）並保持流暢瀏覽。

## 主要功能

- Read-only 載入，不修改原始 CSV
- 分頁讀取與背景索引，避免整檔進記憶體
- DataGrid 虛擬化顯示，適合大檔案
- Filter 條件查詢，支援：
  - `=`, `!=`, `>`, `>=`, `<`, `<=`
- 右鍵複製功能：
  - `Copy Cell`
  - `Copy Row As CSV`（CSV 逗號格式，含必要引號跳脫）
- `Ctrl + C` 複製整列時改為 CSV 格式（不是 tab 分隔）
- 連結跟隨（Follow Link）：
  - 滑到連結欄位可看到提示
  - 按住 `Ctrl` 並點擊可直接用瀏覽器開啟連結
- 右側分割預覽：
  - 雙擊連結可在右側內嵌瀏覽面板開啟
  - 提供返回/前進/重整/外部開啟/關閉
  - 內嵌引擎改為 Edge WebView2（不再使用 IE WebBrowser）

## 使用方式

1. 進入專案：
   - `cd TenhouCsvReader`
2. 建置：
   - `dotnet build`
3. 執行：
   - `dotnet run`
   - 或在根目錄直接執行：
     - `dotnet run --project TenhouCsvReader/TenhouCsvReader.csproj`
4. 在程式中按 `Open CSV` 選擇檔案

## Filter 語法

- 多條件用逗號分隔
- 範例：
  - `prediction = 1, actual = 0, probability > 0.7`
- 欄位名稱大小寫不敏感
- 支援唯一前綴（例如 `pred = 1` 對應 `prediction`）

## 圖示與 Windows 相容性

- 來源圖：`images/cover.png`
- 已轉為多尺寸圖示：`TenhouCsvReader/Assets/app.ico`
- 內含常見尺寸（16, 20, 24, 32, 40, 48, 64, 96, 128, 256）
- 專案已設定：
  - EXE 圖示：`ApplicationIcon`
  - 視窗圖示：`Icon="Assets/app.ico"`

這樣可以避免在不同 DPI / 縮放比例下圖示過小或失真。

## 發佈輸出

- 已建立發佈版本：
  - `TenhouCsvReader/bin/Release/net9.0-windows/publish/`

## WebView2 注意事項

- 右側內嵌瀏覽需要 Microsoft Edge WebView2 Runtime。
- 大多數 Windows 10/11 已內建；若目標機器沒有，安裝後即可使用內嵌瀏覽。

若你要我再幫你做安裝包（例如 `MSIX` 或 `Inno Setup`），我可以直接接著加。
