using Infrastructure.Persistence;
using Infrastructure.TargetDb;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// تسجيل قاعدة بياناتنا الجديدة (Code First)
var appDbConnectionString = builder.Configuration.GetConnectionString("AppDbContextConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(appDbConnectionString));

// إضافة خدمة "Scoped" لاختبار اتصال الـ POS
builder.Services.AddScoped<PosConnectionTester>(sp =>
    new PosConnectionTester(builder.Configuration.GetConnectionString("PosReadOnlyConnection")));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
