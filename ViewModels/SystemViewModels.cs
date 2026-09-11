using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Splitbill.ViewModels;

public sealed class SystemBackupViewModel
{
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
}

public sealed class SystemRestoreViewModel
{
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Required] public IFormFile? BackupFile { get; set; }
}
