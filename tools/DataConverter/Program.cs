using DataConverter.Converters;

// 기본 경로: 메인 솔루션 루트 기준
// tools/ 솔루션에서 실행 시 working dir = tools/
// dotnet run --project DataConverter 실행 시 ../../Bin/resources 로 출력
const string DefaultExcelDir  = "../../Bin/excel";
const string DefaultOutputDir = "../../Bin/resources";

string excelDir  = args.Length > 0 ? args[0] : DefaultExcelDir;
string outputDir = args.Length > 1 ? args[1] : DefaultOutputDir;

Console.WriteLine($"[DataConverter] Excel  : {Path.GetFullPath(excelDir)}");
Console.WriteLine($"[DataConverter] Output : {Path.GetFullPath(outputDir)}");

try
{
    TableConverter.RunAll(excelDir, outputDir);
    Console.WriteLine("[DataConverter] 완료.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[DataConverter] 오류: {ex.Message}");
    Environment.Exit(1);
}
