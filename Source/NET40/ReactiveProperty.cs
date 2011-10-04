﻿using System;
using System.Linq.Expressions;
using System.Linq;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics.Contracts;
using Codeplex.Reactive.Extensions;
#if WINDOWS_PHONE
using Microsoft.Phone.Reactive;
using SerialDisposable = Microsoft.Phone.Reactive.MutableDisposable;
#else
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Disposables;
using System.Reactive.Concurrency;
#endif
#if !WP_COMMON
using System.ComponentModel.DataAnnotations;
#endif

namespace Codeplex.Reactive
{
    internal class SingletonPropertyChangedEventArgs
    {
        public static readonly PropertyChangedEventArgs Value = new PropertyChangedEventArgs("Value");
    }
#if SILVERLIGHT
    internal class SingletonDataErrorsChangedEventArgs
    {
        public static readonly DataErrorsChangedEventArgs Value = new DataErrorsChangedEventArgs("Value");
    }
#endif

    [Flags]
    public enum ReactivePropertyMode
    {
        None = 0x00,
        /// <summary>If next value is same as current, not set and not notify.</summary>
        DistinctUntilChanged = 0x01,
        /// <summary>Push notify on instance created and subscribed.</summary>
        RaiseLatestValueOnSubscribe = 0x02
    }

    // for EventToReactive and Serialization
    public interface IValue
    {
        object Value { get; set; }
    }

    /// <summary>
    /// Two-way bindable IObserable&lt;T&gt;
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ReactiveProperty<T> : IObservable<T>, IDisposable, INotifyPropertyChanged, IValue, IDataErrorInfo
#if SILVERLIGHT
, INotifyDataErrorInfo
#endif
    {
        public event PropertyChangedEventHandler PropertyChanged;

        T latestValue;
        readonly IScheduler raiseEventScheduler;
        readonly IObservable<T> source;
        readonly Subject<T> anotherTrigger = new Subject<T>();
        readonly IDisposable sourceDisposable;
        readonly IDisposable raiseSubscription;

        // for Validation
        bool isValueChanged = false;
        readonly SerialDisposable validateNotifyErrorSubscription = new SerialDisposable();
        readonly BehaviorSubject<object> errorsTrigger = new BehaviorSubject<object>(null);

        /// <summary>PropertyChanged raise on UIDispatcherScheduler</summary>
        public ReactiveProperty(T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this(UIDispatcherScheduler.Default, initialValue, mode)
        { }

        /// <summary>PropertyChanged raise on selected scheduler</summary>
        public ReactiveProperty(IScheduler raiseEventScheduler, T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this((Action<T>)null, raiseEventScheduler, initialValue, mode)
        {
            Contract.Requires<ArgumentNullException>(raiseEventScheduler != null);
        }

        /// <summary>PropertyChanged raise on UIDispatcherScheduler</summary>
        public ReactiveProperty(Action<T> parentRaisePropertyChanged, T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this(parentRaisePropertyChanged, UIDispatcherScheduler.Default, initialValue, mode)
        { }

        /// <summary>PropertyChanged raise on selected scheduler</summary>
        public ReactiveProperty(Action<T> parentRaisePropertyChanged, IScheduler raiseEventScheduler, T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this(Observable.Never<T>(), parentRaisePropertyChanged, raiseEventScheduler, initialValue, mode)
        {
            Contract.Requires<ArgumentNullException>(raiseEventScheduler != null);
        }

        // ToReactiveProperty Only
        internal ReactiveProperty(IObservable<T> source, T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this(source, (Action<T>)null, initialValue, mode)
        {
            Contract.Requires<ArgumentNullException>(source != null);
        }

        internal ReactiveProperty(IObservable<T> source, IScheduler raiseEventScheduler, T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this(source, null, raiseEventScheduler, initialValue, mode)
        {
            Contract.Requires<ArgumentNullException>(source != null);
            Contract.Requires<ArgumentNullException>(raiseEventScheduler != null);
        }

        internal ReactiveProperty(
            IObservable<T> source, Action<T> parentRaisePropertyChanged, T initialValue = default(T), ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
            : this(source, parentRaisePropertyChanged, UIDispatcherScheduler.Default, initialValue, mode)
        {
            Contract.Requires<ArgumentNullException>(source != null);
        }

        internal ReactiveProperty(
            IObservable<T> source, Action<T> parentRaisePropertyChanged,
            IScheduler raiseEventScheduler, T initialValue = default(T),
            ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
        {
            Contract.Requires<ArgumentNullException>(source != null);
            Contract.Requires<ArgumentNullException>(raiseEventScheduler != null);

            this.latestValue = initialValue;
            this.raiseEventScheduler = raiseEventScheduler;

            // create source
            var merge = source.Merge(anotherTrigger);
            if (mode.HasFlag(ReactivePropertyMode.DistinctUntilChanged)) merge = merge.DistinctUntilChanged();
            merge = merge.Do(x => latestValue = x); // setvalue immediately

            // publish observable
            var connectable = (mode.HasFlag(ReactivePropertyMode.RaiseLatestValueOnSubscribe))
                ? merge.Publish(initialValue)
                : merge.Publish();
            this.source = connectable.AsObservable();

            // raise notification
            this.raiseSubscription = connectable
                .ObserveOn(raiseEventScheduler)
                .Subscribe(x =>
                {
                    if (!isValueChanged) isValueChanged = true;

                    var handler = PropertyChanged;
                    if (handler != null) PropertyChanged(this, SingletonPropertyChangedEventArgs.Value);
                    if (parentRaisePropertyChanged != null) parentRaisePropertyChanged(x);
                });

            // start source
            this.sourceDisposable = connectable.Connect();
        }

        /// <summary>
        /// Get latestValue or push(set) value.
        /// </summary>
        public T Value
        {
            get { return latestValue; }
            set { anotherTrigger.OnNext(value); }
        }

        object IValue.Value
        {
            get { return (T)Value; }
            set { Value = (T)value; }
        }

        /// <summary>
        /// Subscribe source.
        /// </summary>
        public IDisposable Subscribe(IObserver<T> observer)
        {
            return source.Subscribe(observer);
        }

        /// <summary>
        /// Unsubcribe all subscription.
        /// </summary>
        public void Dispose()
        {
            raiseSubscription.Dispose();
            sourceDisposable.Dispose();
            validateNotifyErrorSubscription.Dispose();
            errorsTrigger.Dispose();
        }

        public override string ToString()
        {
            return (latestValue == null)
                ? "null"
                : "{" + latestValue.GetType().Name + ":" + latestValue.ToString() + "}";
        }

        // Validations

        /// <summary>
        /// <para>Checked validation, raised value. If success return value is null.</para>
        /// <para>From Attribute is Exception, from IDataErrorInfo is string, from IDataNotifyErrorInfo is Enumerable.</para>
        /// <para>If you want to assort type, please choose OfType. For example: ErrorsChanged.OfType&lt;string&gt;().</para>
        /// </summary>
        public IObservable<object> ObserveErrorChanged
        {
            get { return errorsTrigger.AsObservable(); }
        }

        // Exception

#if! WP_COMMON
        ValidationContext validationContext;
        ValidationAttribute[] attributes;

        /// <summary>
        /// <para>Set DataAnnotaion's validation.</para>
        /// <para>Note:This validation check by IDataErrorInfo. Please turn on XAML's ValidatesOnDataErrors</para>
        /// </summary>
        /// <param name="selfSelector">Self selector. For example: () =&gt; this.MyProperty</param>
        public ReactiveProperty<T> SetValidateAttribute(Expression<Func<ReactiveProperty<T>>> selfSelector)
        {
            Contract.Requires<ArgumentNullException>(selfSelector != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            this.attributes = ((MemberExpression)selfSelector.Body).Member
                .GetCustomAttributes(typeof(ValidationAttribute), true)
                .Cast<ValidationAttribute>()
                .ToArray();
            return this;
        }

        string ValidateException()
        {
            try
            {
                if (validationContext == null)
                {
                    validationContext = new ValidationContext(this, null, null) { MemberName = "Value" };
                }

                foreach (var item in attributes)
                {
                    item.Validate(latestValue, validationContext);
                }

                return null;
            }
            catch (Exception ex)
            {
                errorsTrigger.OnNext(ex);
                return ex.Message;
            }
        }
#endif

        // IDataErrorInfo

        Func<T, string> dataErrorInfoValidate;
        string currentError;

        /// <summary>
        /// <para>Set IDataErrorInfo's validation.</para>
        /// </summary>
        /// <param name="validate">If success return null, failure return string(ErrorMessage).</param>
        public ReactiveProperty<T> SetValidateError(Func<T, string> validate)
        {
            Contract.Requires<ArgumentNullException>(validate != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            this.dataErrorInfoValidate = validate;
            return this;
        }

        /// <summary>Get IDataErrorInfo's error store</summary>
        public string Error
        {
            get { return currentError; }
        }

        string IDataErrorInfo.this[string columnName]
        {
            get
            {
                if (!isValueChanged) return null;

#if! WP_COMMON
                if (attributes != null && columnName == "Value")
                {
                    var exceptionResult = ValidateException();
                    if (exceptionResult != null)
                    {
                        return exceptionResult;
                    }
                }
#endif

                var handler = dataErrorInfoValidate;
                if (handler != null && columnName == "Value")
                {
                    currentError = handler(latestValue);
                    errorsTrigger.OnNext(currentError);
                    return currentError;
                }

                errorsTrigger.OnNext(null);
                return null;
            }
        }

#if SILVERLIGHT
        // INotifyDataErrorInfo

        IEnumerable currentErrors;
        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;

        /// <summary>
        /// <para>Set INotifyDataErrorInfo's asynchronous validation.</para>
        /// </summary>
        /// <param name="validate">Argument is self. If success return IO&lt;null&gt;, failure return IO&lt;IEnumerable&gt;(Errors).</param>
        public ReactiveProperty<T> SetValidateNotifyError(Func<IObservable<T>, IObservable<IEnumerable>> validate)
        {
            Contract.Requires<ArgumentNullException>(validate != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            validateNotifyErrorSubscription.Disposable = validate(source)
                .Subscribe(xs =>
                {
                    currentErrors = xs;
                    var handler = ErrorsChanged;
                    if (handler != null)
                    {
                        raiseEventScheduler.Schedule(() =>
                            handler(this, SingletonDataErrorsChangedEventArgs.Value));
                    }
                    errorsTrigger.OnNext(currentErrors);
                });

            return this;
        }

        /// <summary>Get INotifyDataErrorInfo's error store</summary>
        public System.Collections.IEnumerable GetErrors(string propertyName)
        {
            return currentErrors;
        }

        /// <summary>Get INotifyDataErrorInfo's error store</summary>
        public bool HasErrors
        {
            get { return currentErrors != null; }
        }

#endif
    }

    public static class ReactivePropertyObservableExtensions
    {
        /// <summary>
        /// <para>Convert to two-way bindable IObservable&lt;T&gt;</para>
        /// <para>PropertyChanged raise on UIDispatcherScheduler</para>
        /// </summary>
        public static ReactiveProperty<T> ToReactiveProperty<T>(this IObservable<T> source,
            T initialValue = default(T),
            ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
        {
            Contract.Requires<ArgumentNullException>(source != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            return new ReactiveProperty<T>(source, initialValue, mode);
        }

        /// <summary>
        /// <para>Convert to two-way bindable IObservable&lt;T&gt;</para>
        /// <para>PropertyChanged raise on selected scheduler</para>
        /// </summary>
        public static ReactiveProperty<T> ToReactiveProperty<T>(this IObservable<T> source,
            IScheduler raiseEventScheduler, T initialValue = default(T),
            ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
        {
            Contract.Requires<ArgumentNullException>(source != null);
            Contract.Requires<ArgumentNullException>(raiseEventScheduler != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            return new ReactiveProperty<T>(source, raiseEventScheduler, initialValue, mode);
        }

        /// <summary>
        /// <para>Convert to two-way bindable IObservable&lt;T&gt;</para>
        /// <para>PropertyChanged raise on UIDispatcherScheduler</para>
        /// </summary>
        public static ReactiveProperty<T> ToReactiveProperty<T>(this IObservable<T> source,
            Action<T> parentRaisePropertyChanged, T initialValue = default(T),
            ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
        {
            Contract.Requires<ArgumentNullException>(source != null);
            Contract.Requires<ArgumentNullException>(parentRaisePropertyChanged != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            return new ReactiveProperty<T>(source, parentRaisePropertyChanged, initialValue, mode);
        }

        /// <summary>
        /// <para>Convert to two-way bindable IObservable&lt;T&gt;</para>
        /// <para>PropertyChanged raise on selected scheduler</para>
        /// </summary>
        public static ReactiveProperty<T> ToReactiveProperty<T>(this IObservable<T> source,
            Action<T> parentRaisePropertyChanged, IScheduler raiseEventScheduler, T initialValue = default(T),
            ReactivePropertyMode mode = ReactivePropertyMode.DistinctUntilChanged)
        {
            Contract.Requires<ArgumentNullException>(source != null);
            Contract.Requires<ArgumentNullException>(parentRaisePropertyChanged != null);
            Contract.Requires<ArgumentNullException>(raiseEventScheduler != null);
            Contract.Ensures(Contract.Result<ReactiveProperty<T>>() != null);

            return new ReactiveProperty<T>(source, parentRaisePropertyChanged, raiseEventScheduler, initialValue, mode);
        }
    }
}