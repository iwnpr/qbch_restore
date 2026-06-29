using Confluent.Kafka;

namespace QbchRequestRestore.Kafka;

/// <summary>
/// Отправка события в Kafka так же, как qbch_api (см. KafkaService.Produce):
/// сообщение Message&lt;Null, string&gt; со значением-ключом "QBCH:dlrequest:&lt;guid&gt;", Acks.All.
/// </summary>
public sealed class KafkaSender : IDisposable
{
    private readonly IProducer<Null, string> _producer;
    private readonly string _topic;

    public KafkaSender(string bootstrapServers, string topic)
    {
        _topic = topic;
        _producer = new ProducerBuilder<Null, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            LingerMs = 0,
            Acks = Acks.All
        }).Build();
    }

    /// <summary>Отправить ключ в топик и дождаться подтверждения. Бросает исключение при ошибке доставки.</summary>
    public async Task ProduceAsync(string key)
    {
        await _producer.ProduceAsync(_topic, new Message<Null, string> { Value = key });
    }

    public void Dispose() => _producer.Dispose();
}
