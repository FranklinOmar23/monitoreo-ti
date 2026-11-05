// El estado TLS del agente es estático: las pruebas no corren en paralelo.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
