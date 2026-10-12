using DataPreprocessingTask.Steps;

Console.WriteLine("MMORS Data Preprocessing Pipeline");
Console.WriteLine();

Inspector.Run();
Extractor.Run();
Assessor.Run();
Cleaner.Run();
Missingness.Run();
Imputer.Run();
Transformer.Run();
LogBuilder.Run();
Validator.Run();
Figures.Run();