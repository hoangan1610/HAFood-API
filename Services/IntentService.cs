using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML;
using HaFood.IntentClassifier; // ModelInput, ModelOutput, RegexFeatsFactory

namespace HAShop.Api.Services
{
    public sealed class IntentService : IIntentService, IDisposable
    {
        private readonly string _modelPath;
        private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

        private MLContext _ml = null!;
        private ITransformer _model = null!;
        private PredictionEngine<ModelInput, ModelOutput> _engine = null!;

        public IntentService(string modelPath)
        {
            _modelPath = modelPath;
            (_ml, _model, _engine) = LoadCore(_modelPath);
        }

        public (string intent, float confidence) PredictIntent(string q, string? a = null)
        {
            _gate.EnterReadLock();
            try
            {
                var input = new ModelInput { Q = q ?? string.Empty, A = a ?? string.Empty, Intent = "" };
                var output = _engine.Predict(input);
                var conf = (output.Scores ?? Array.Empty<float>()).DefaultIfEmpty(0f).Max();
                return (output.PredictedIntent ?? "", conf);
            }
            finally { _gate.ExitReadLock(); }
        }

        public void Reload()
        {
            _gate.EnterWriteLock();
            try
            {
                _engine?.Dispose();
                (_ml, _model, _engine) = LoadCore(_modelPath);
            }
            finally { _gate.ExitWriteLock(); }
        }

        private static (MLContext, ITransformer, PredictionEngine<ModelInput, ModelOutput>) LoadCore(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Intent model not found", path);

            var ml = new MLContext();
            // Quan trọng: đăng ký CustomMappingFactory trước khi load
            ml.ComponentCatalog.RegisterAssembly(typeof(RegexFeatsFactory).Assembly);

            using var fs = File.OpenRead(path);
            var model = ml.Model.Load(fs, out _);
            var engine = ml.Model.CreatePredictionEngine<ModelInput, ModelOutput>(model);
            return (ml, model, engine);
        }

        public void Dispose()
        {
            _gate?.Dispose();
            _engine?.Dispose();
        }
    }
}
