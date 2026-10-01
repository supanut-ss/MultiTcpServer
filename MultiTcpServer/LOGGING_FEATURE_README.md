# Disconnected Database Logging - File-Based Storage

## Overview
The TCP Server now supports **dual storage modes** for saving client data:

1. **Database Mode** (Default) - Stores data directly to the database
2. **File Mode (Offline)** - Stores data to CSV files when database is unavailable

## Architecture

### Components

#### IDataRepository Interface
Defines the contract for data storage implementations:
```csharp
public interface IDataRepository
{
	void SaveData(string clientIp, string message);
}
```

#### DatabaseRepository
- Implements database storage using Entity Framework
- Uses the existing `LIMS_BREntities` context
- Stores data in `t_interface_lims_machine_client` table

#### FileRepository
- Implements file-based CSV storage
- Stores data in `Data/ClientData_yyyy-MM-dd.csv` files
- Thread-safe file operations using locks
- Automatic directory creation

## How to Use

### Switching Storage Modes

1. **At Runtime** - Use the radio buttons in the UI:
   - Select "Database" for normal database operation
   - Select "File (Offline)" to switch to disconnected file-based logging

2. **Programmatically**:
```csharp
// Switch to File mode
form.SwitchStorageMode(true);

// Switch back to Database mode
form.SwitchStorageMode(false);
```

### File-Based Storage Details

**Location:** `Application Directory/Data/ClientData_yyyy-MM-dd.csv`

**CSV Format:**
```
data_id,client_ip,data_time,data_message
1,"192.168.1.100","2024-01-15 10:30:45","Client data message"
2,"192.168.1.101","2024-01-15 10:30:46","Another message"
```

**Features:**
- Auto-incremented ID per day
- Thread-safe concurrent writes
- CSV field escaping for special characters
- Automatic header generation for new files
- One file per day for easy organization

### Logging

Both storage modes also maintain separate log files:

- **Location:** `Application Directory/Logs/Log_yyyy-MM-dd.log`
- **Format:** Timestamped entries for all server events
- **Auto-cleanup:** Clears UI after 200 lines

## Error Handling

- If database connection fails while in Database mode, errors are logged
- If file write fails in File mode, errors are logged to the UI
- Smooth fallback mechanism when switching modes

## Benefits

✅ **Disconnected Operation** - Works without database connection  
✅ **Offline Data Collection** - Store data locally when DB is down  
✅ **Easy Mode Switching** - Toggle between modes with radio buttons  
✅ **No Data Loss** - Data persisted in CSV when DB unreachable  
✅ **Thread-Safe** - Safe concurrent writes to files  
✅ **Automatic Cleanup** - Directory and file creation handled automatically  

## Configuration

No additional configuration required. The application:
- Creates `Data/` directory automatically
- Creates CSV files with proper headers
- Manages file handles safely
- Logs all mode switches in the UI

## Example: Dual Operation

When running in File mode:
1. All client data is saved to CSV files
2. All UI logs are saved to the Logs folder
3. View the CSV files in the Data directory
4. Switch back to Database mode when DB is available
5. Manually upload CSV data or process separately

---

**Version:** 1.0  
**Target Framework:** .NET Framework 4.8
