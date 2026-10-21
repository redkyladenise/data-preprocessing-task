using DataPreprocessingTask.Steps;

Console.WriteLine("================================================================");
Console.WriteLine("  MMORS WATER QUALITY PREPROCESSING PIPELINE");
Console.WriteLine("  Dataset: 2012-2018 | Marilao, Meycauayan, Obando");
Console.WriteLine("================================================================");
Console.WriteLine();

Inspector.Run();
Console.WriteLine();

// Extractor.Run();
// Console.WriteLine();

// Assessor.Run();
// Console.WriteLine();

// Cleaner.Run();
// Console.WriteLine();


// Imputer.Run();
// Console.WriteLine();

// Transformer.Run();
// Console.WriteLine();

// LogBuilder.Run();
// Console.WriteLine();

// Validator.Run();
// Console.WriteLine();

// Figures.Run();

// Console.WriteLine();
// Console.WriteLine("================================================================");
// Console.WriteLine("  PIPELINE COMPLETE");
// Console.WriteLine("  Final dataset  : Data/clean/mmors_water_quality_clean_2012_2018.csv");
// Console.WriteLine("  Cleaning log   : Data/clean/mmors_consolidated_cleaning_log_2012_2018.csv");
// Console.WriteLine("  Validation     : Output/validation_report.csv");
// Console.WriteLine("  Figures        : Output/figures/");
// Console.WriteLine("================================================================");