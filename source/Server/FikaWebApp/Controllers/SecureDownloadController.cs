using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FikaWebApp.Controllers;

[Route("api/secure-download")]
[ApiController]
public class SecureDownloadController : ControllerBase
{
    [HttpGet("{*filename}")]
    [Authorize]
    public IActionResult GetFile(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return BadRequest("Filename is required.");
        }

        // 存放受保护文件的根目录
        var rootPath = Path.GetFullPath("data/protectedfiles");

        // 把根路径与请求的文件名组合起来
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, filename));

        // 阻止访问 ProtectedFiles 之外的路径
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid path");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        const string contentType = "application/octet-stream";

        // 只返回真正的文件名，而不是完整的相对路径
        return PhysicalFile(fullPath, contentType, Path.GetFileName(fullPath));
    }
}
