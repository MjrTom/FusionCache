﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ZiggyCreatures.Caching.Fusion.Backplane.Memory;

/// <summary>
/// An in-memory implementation of a FusionCache backplane
/// </summary>
public class MemoryBackplane
	: IFusionCacheBackplane
{
	private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<MemoryBackplane>>> _connections = new ConcurrentDictionary<string, ConcurrentDictionary<string, List<MemoryBackplane>>>();

	private readonly MemoryBackplaneOptions _options;
	private BackplaneSubscriptionOptions? _subscriptionOptions;
	private readonly ILogger? _logger;

	private ConcurrentDictionary<string, List<MemoryBackplane>>? _connection;

	private string? _channelName = null;
	private List<MemoryBackplane>? _subscribers;

	private Action<BackplaneConnectionInfo>? _connectHandler;
	private Action<BackplaneMessage>? _incomingMessageHandler;
	private Func<BackplaneConnectionInfo, ValueTask>? _connectHandlerAsync;
	private Func<BackplaneMessage, ValueTask>? _incomingMessageHandlerAsync;

	/// <summary>
	/// Initializes a new instance of the MemoryBackplane class.
	/// </summary>
	/// <param name="optionsAccessor">The set of options to use with this instance of the backplane.</param>
	/// <param name="logger">The <see cref="ILogger{TCategoryName}"/> instance to use. If null, logging will be completely disabled.</param>
	public MemoryBackplane(IOptions<MemoryBackplaneOptions> optionsAccessor, ILogger<MemoryBackplane>? logger = null)
	{
		if (optionsAccessor is null)
			throw new ArgumentNullException(nameof(optionsAccessor));

		// OPTIONS
		_options = optionsAccessor.Value ?? throw new ArgumentNullException(nameof(optionsAccessor.Value));


		// LOGGING
		if (logger is NullLogger<MemoryBackplane>)
		{
			// IGNORE NULL LOGGER (FOR BETTER PERF)
			_logger = null;
		}
		else
		{
			_logger = logger;
		}

		// CONNECTION
		if (_options.ConnectionId is null)
		{
			if (_logger?.IsEnabled(LogLevel.Warning) ?? false)
				_logger.Log(LogLevel.Warning, "FUSION: [BP] A MemoryBackplane should be used with an explicit ConnectionId option, otherwise concurrency issues will probably happen");

			_options.ConnectionId = "_default";
		}
	}

	private void EnsureConnection()
	{
		if (_options.ConnectionId is null)
			throw new NullReferenceException("The ConnectionId is null");

		if (_connection is null)
		{
			_connection = _connections.GetOrAdd(_options.ConnectionId, _ => new ConcurrentDictionary<string, List<MemoryBackplane>>());
			_connectHandler?.Invoke(new BackplaneConnectionInfo(false));
		}

		EnsureSubscribers();
	}

	private void EnsureSubscribers()
	{
		if (_connection is null)
			throw new InvalidOperationException("No connection available");

		if (_subscribers is null)
		{
			lock (_connection)
			{
				if (_channelName is null)
					throw new NullReferenceException("The backplane channel name is null");

				_subscribers = _connection.GetOrAdd(_channelName, _ => []);
			}
		}
	}

	private void Disconnect()
	{
		_connectHandler = null;
		_connectHandlerAsync = null;

		if (_connection is null)
			return;

		_connection = null;
	}

	/// <inheritdoc/>
	public void Subscribe(BackplaneSubscriptionOptions subscriptionOptions)
	{
		if (subscriptionOptions is null)
			throw new ArgumentNullException(nameof(subscriptionOptions));

		if (subscriptionOptions.ChannelName is null)
			throw new NullReferenceException("The BackplaneSubscriptionOptions.ChannelName cannot be null");

		if (subscriptionOptions.IncomingMessageHandler is null)
			throw new NullReferenceException("The BackplaneSubscriptionOptions.IncomingMessageHandler cannot be null");

		if (subscriptionOptions.ConnectHandler is null)
			throw new NullReferenceException("The BackplaneSubscriptionOptions.ConnectHandler cannot be null");

		if (subscriptionOptions.IncomingMessageHandlerAsync is null)
			throw new NullReferenceException("The BackplaneSubscriptionOptions.IncomingMessageHandlerAsync cannot be null");

		if (subscriptionOptions.ConnectHandlerAsync is null)
			throw new NullReferenceException("The BackplaneSubscriptionOptions.ConnectHandlerAsync cannot be null");

		_subscriptionOptions = subscriptionOptions;

		_channelName = _subscriptionOptions.ChannelName;

		_incomingMessageHandler = _subscriptionOptions.IncomingMessageHandler;
		_connectHandler = _subscriptionOptions.ConnectHandler;
		_incomingMessageHandlerAsync = _subscriptionOptions.IncomingMessageHandlerAsync;
		_connectHandlerAsync = _subscriptionOptions.ConnectHandlerAsync;

		// CONNECTION
		EnsureConnection();

		if (_subscribers is null)
			throw new InvalidOperationException("The subscriber is null");

		lock (_subscribers)
		{
			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] before subscribing (Subscribers: {SubscribersCount})", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, _subscribers.Count);

			_subscribers.Add(this);

			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] after subscribing (Subscribers: {SubscribersCount})", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, _subscribers.Count);
		}
	}

	/// <inheritdoc/>
	public void Unsubscribe()
	{
		if (_subscribers is not null)
		{
			_incomingMessageHandler = null;
			_incomingMessageHandlerAsync = null;

			lock (_subscribers)
			{
				if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
					_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] before unsubscribing (Subscribers: {SubscribersCount})", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, _subscribers.Count);

				var removed = _subscribers.Remove(this);

				if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
					_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}]: [BP] after unsubscribing (Subscribers: {SubscribersCount}, Removed: {Removed})", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, _subscribers.Count, removed);

				_subscriptionOptions = null;
				_channelName = null;

				_incomingMessageHandler = null;
				_connectHandler = null;
				_incomingMessageHandlerAsync = null;
				_connectHandlerAsync = null;
			}

			_subscribers = null;
		}

		Disconnect();
	}

	/// <inheritdoc/>
	public async ValueTask PublishAsync(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
	{
		EnsureConnection();

		if (message is null)
			throw new ArgumentNullException(nameof(message));

		if (message.IsValid() == false)
			throw new InvalidOperationException("The message is invalid");

		if (_subscribers is null)
			throw new NullReferenceException("Something went wrong :-|");

		if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
			_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] about to send a backplane notification to {BackplanesCount} backplanes (including self)", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, _subscribers.Count);

		var payload = BackplaneMessage.ToByteArray(message);

		foreach (var backplane in _subscribers)
		{
			token.ThrowIfCancellationRequested();

			if (backplane == this)
				continue;

			try
			{
				if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
					_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] before sending a backplane notification to channel {BackplaneChannel}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, backplane._channelName);

				await backplane.OnMessageAsync(payload).ConfigureAwait(false);

				if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
					_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] after sending a backplane notification to channel {BackplaneChannel}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, backplane._channelName);
			}
			catch
			{
				if (_logger?.IsEnabled(LogLevel.Error) ?? false)
					_logger.Log(LogLevel.Error, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] An error occurred while publishing a message to a subscriber", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey);
			}
		}
	}

	/// <inheritdoc/>
	public void Publish(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
	{
		EnsureConnection();

		if (message is null)
			throw new ArgumentNullException(nameof(message));

		if (message.IsValid() == false)
			throw new InvalidOperationException("The message is invalid");

		if (_subscribers is null)
			throw new NullReferenceException("Something went wrong :-|");

		if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
			_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] about to send a backplane notification to {BackplanesCount} backplanes (including self)", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, _subscribers.Count);

		var payload = BackplaneMessage.ToByteArray(message);

		foreach (var backplane in _subscribers)
		{
			token.ThrowIfCancellationRequested();

			if (backplane == this)
				continue;

			try
			{
				if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
					_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] before sending a backplane notification to channel {BackplaneChannel}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, backplane._channelName);

				backplane.OnMessage(payload);

				if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
					_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] after sending a backplane notification to channel {BackplaneChannel}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, backplane._channelName);
			}
			catch
			{
				if (_logger?.IsEnabled(LogLevel.Error) ?? false)
					_logger.Log(LogLevel.Error, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] An error occurred while publishing a message to a subscriber", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey);
			}
		}
	}

	internal async ValueTask OnMessageAsync(byte[] payload)
	{
		var message = BackplaneMessage.FromByteArray(payload);

		var handler = _incomingMessageHandlerAsync;

		if (handler is null)
		{
			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] a backplane notification received from {BackplaneMessageSourceId} will not be processed because the handler is null", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, message.SourceId);
		}
		else
		{
			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] before processing a backplane notification received from {BackplaneMessageSourceId}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, message.SourceId);

			await handler(message).ConfigureAwait(false);

			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] after processing a backplane notification received from {BackplaneMessageSourceId}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, message.SourceId);
		}
	}

	internal void OnMessage(byte[] payload)
	{
		var message = BackplaneMessage.FromByteArray(payload);

		var handler = _incomingMessageHandler;

		if (handler is null)
		{
			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] a backplane notification received from {BackplaneMessageSourceId} will not be processed because the handler is null", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, message.SourceId);
		}
		else
		{
			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] before processing a backplane notification received from {BackplaneMessageSourceId}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, message.SourceId);

			handler(message);

			if (_logger?.IsEnabled(LogLevel.Trace) ?? false)
				_logger.Log(LogLevel.Trace, "FUSION [N={CacheName} I={CacheInstanceId}] (K={CacheKey}): [BP] after processing a backplane notification received from {BackplaneMessageSourceId}", _subscriptionOptions?.CacheName, _subscriptionOptions?.CacheInstanceId, message.CacheKey, message.SourceId);
		}
	}
}
