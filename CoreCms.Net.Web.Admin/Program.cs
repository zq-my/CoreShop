using Autofac.Extensions.DependencyInjection;
using CoreCms.Net.Loging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Web;
using System;
using System.Linq;
using Autofac;
using CoreCms.Net.Auth;
using CoreCms.Net.Configuration;
using CoreCms.Net.Core.AutoFac;
using CoreCms.Net.Core.Config;
using CoreCms.Net.Filter;
using CoreCms.Net.Mapping;
using CoreCms.Net.Middlewares;
using CoreCms.Net.Swagger;
using Essensoft.Paylink.Alipay;
using Essensoft.Paylink.WeChatPay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Yitter.IdGenerator;


var builder = WebApplication.CreateBuilder(args);

//添加本地路径获取支持
builder.Services.AddSingleton(new AppSettingsHelper(builder.Environment.ContentRootPath));
builder.Services.AddSingleton(new LogLockHelper(builder.Environment.ContentRootPath));

//Memory缓存
builder.Services.AddMemoryCacheSetup();
//Redis缓存
builder.Services.AddRedisCacheSetup();

//添加数据库连接SqlSugar注入支持
builder.Services.AddSqlSugarSetup();
//配置跨域（CORS）
builder.Services.AddCorsSetup();

//添加session支持(session依赖于cache进行存储)
builder.Services.AddSession();
// AutoMapper支持
builder.Services.AddAutoMapper(typeof(AutoMapperConfiguration));

//使用 SignalR
builder.Services.AddSignalR();

// 引入Payment 依赖注入(支付宝支付/微信支付)
builder.Services.AddAlipay();
builder.Services.AddWeChatPay();

// 在 appsettings.json 中 配置选项
builder.Services.Configure<WeChatPayOptions>(builder.Configuration.GetSection("WeChatPay"));
builder.Services.Configure<AlipayOptions>(builder.Configuration.GetSection("Alipay"));

//注册自定义微信接口配置文件
builder.Services.Configure<CoreCms.Net.WeChat.Service.Options.WeChatOptions>(builder.Configuration.GetSection(nameof(CoreCms.Net.WeChat.Service.Options.WeChatOptions)));

// 注入工厂 HTTP 客户端
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CoreCms.Net.WeChat.Service.HttpClients.IWeChatApiHttpClientFactory, CoreCms.Net.WeChat.Service.HttpClients.WeChatApiHttpClientFactory>();

//Swagger接口文档注入
builder.Services.AddAdminSwaggerSetup();

//配置易联云打印机
builder.Services.AddYiLianYunSetup();

//jwt授权支持注入
builder.Services.AddAuthorizationSetupForAdmin();
//上下文注入
builder.Services.AddHttpContextSetup();

//服务配置中加入AutoFac控制器替换规则。
builder.Services.Replace(ServiceDescriptor.Transient<IControllerActivator, ServiceBasedControllerActivator>());

//注册mvc，注册razor引擎视图
builder.Services.AddMvc(options =>
{
    //实体验证
    options.Filters.Add<RequiredErrorForAdmin>();
    //异常处理
    options.Filters.Add<GlobalExceptionsFilterForAdmin>();
    //Swagger剔除不需要加入api展示的列表
    options.Conventions.Add(new ApiExplorerIgnores());

    options.EnableEndpointRouting = false;
})
    .AddNewtonsoftJson(p =>
    {
        //数据格式首字母小写 不使用驼峰
        p.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        //不使用驼峰样式的key
        //p.SerializerSettings.ContractResolver = new DefaultContractResolver();
        //忽略循环引用
        p.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        //设置时间格式
        p.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss";
    });


// 雪花漂移算法
// 创建 IdGeneratorOptions 对象，请在构造函数中输入 WorkerId：
var options = new IdGeneratorOptions(1);
// 保存参数（必须的操作，否则以上设置都不能生效）：
YitIdHelper.SetIdGenerator(options);

#region AutoFac注册============================================================================

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
{
    //获取所有控制器类型并使用属性注入
    var controllerBaseType = typeof(ControllerBase);
    containerBuilder.RegisterAssemblyTypes(typeof(Program).Assembly)
        .Where(t => controllerBaseType.IsAssignableFrom(t) && t != controllerBaseType)
        .PropertiesAutowired();

    containerBuilder.RegisterModule(new AutofacModuleRegister());

});

#endregion

var app = builder.Build();

#region 解决Ubuntu Nginx 代理不能获取IP问题
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
#endregion

// 记录请求与返回数据 (注意开启权限，不然本地无法写入)
app.UseRequestResponseLog();
// 用户访问记录(必须放到外层，不然如果遇到异常，会报错，因为不能返回流)(注意开启权限，不然本地无法写入)
app.UseRecordAccessLogsMildd();
// 记录ip请求 (注意开启权限，不然本地无法写入)
app.UseIpLogMildd();
// signalr
app.UseSignalRSendMildd();

app.UseSwagger().UseSwaggerUI(c =>
{
    //根据版本名称倒序 遍历展示
    typeof(CustomApiVersion.ApiVersions).GetEnumNames().OrderByDescending(e => e).ToList().ForEach(
        version =>
        {
            c.SwaggerEndpoint($"/swagger/{version}/swagger.json", $"Doc {version}");
        });
    c.RoutePrefix = "doc";
});

//使用 Session
app.UseSession();

if (app.Environment.IsDevelopment())
{
    // 在开发环境中，使用异常页面，这样可以暴露错误堆栈信息，所以不要放在生产环境。
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// CORS跨域
app.UseCors(AppSettingsConstVars.CorsPolicyName);
// 跳转https
//app.UseHttpsRedirection();
// 使用静态文件
app.UseStaticFiles();
// 使用cookie
app.UseCookiePolicy();
// 返回错误码
app.UseStatusCodePages();
// Routing
app.UseRouting();
// 先开启认证
app.UseAuthentication();
// 然后是授权中间件
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

//设置默认起始页（如default.html）
//此处的路径是相对于wwwroot文件夹的相对路径
var defaultFilesOptions = new DefaultFilesOptions();
defaultFilesOptions.DefaultFileNames.Clear();
defaultFilesOptions.DefaultFileNames.Add("index.html");
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles();

try
{
    //确保NLog.config中连接字符串与appsettings.json中同步
    NLogUtil.EnsureNlogConfig("NLog.config");
    //其他项目启动时需要做的事情
    NLogUtil.WriteAll(NLog.LogLevel.Trace, LogType.ApiRequest, "接口启动", "接口启动成功");

    app.Run();
}
catch (Exception ex)
{
    //使用Nlog写到本地日志文件（万一数据库没创建/连接成功）
    NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.ApiRequest, "接口启动", "初始化数据异常", ex);
    throw;
}
