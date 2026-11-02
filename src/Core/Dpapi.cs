using System;
using System.Security.Cryptography;
using System.Text;

namespace Curl2Url
{
    internal static class Dpapi
    {
        public static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] data = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(
                    Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                // 换 Windows 用户或换机器后解不开，留空让用户重新填写
                return "";
            }
        }
    }
}
