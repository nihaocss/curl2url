# 第三方组件声明

`curl2url.exe` 内嵌了以下第三方托管程序集，用于连接 MySQL。它们不会单独出现在发布目录中，而是作为 exe 资源在运行时加载。

| 组件 | 版本 | 许可 | 项目地址 |
|---|---|---|---|
| MySqlConnector | 1.3.14 | MIT | https://github.com/mysql-net/MySqlConnector |
| System.Buffers | 4.5.1（程序集文件版本 4.6.28619.01） | MIT | https://github.com/dotnet/corefx |
| System.Memory | 4.5.5（程序集文件版本 4.6.31308.01） | MIT | https://github.com/dotnet/corefx |
| System.Runtime.CompilerServices.Unsafe | 4.5.3（程序集文件版本 4.6.28619.01） | MIT | https://github.com/dotnet/corefx |
| System.Threading.Tasks.Extensions | 4.5.4（程序集文件版本 4.6.28619.01） | MIT | https://github.com/dotnet/corefx |

MySQL 驱动版权声明：Copyright © 2016–2021 Bradley Grainger。

.NET 组件版权声明：Copyright © Microsoft Corporation. All rights reserved.

Redis 客户端（RESP2、SCAN、Cluster 路由）为本项目自行实现，不包含 StackExchange.Redis 或其他 Redis 客户端代码。

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
