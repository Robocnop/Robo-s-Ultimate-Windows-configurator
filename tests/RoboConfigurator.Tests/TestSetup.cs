using Xunit;

// Lang.French est statique : les tests ne doivent pas tourner en parallèle
[assembly: CollectionBehavior(DisableTestParallelization = true)]
