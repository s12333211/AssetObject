using System;
using System.Collections.Generic;
using UniRx;
using UniRx.Async;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

[Serializable]
public class AssetDownloader
{
    [SerializeField] protected string key = null;
    protected UniTaskCompletionSource source;
    const float percentCompleteBase = 0.75f;    //バージョン1.10.0のAddressablesのダウンロード進捗度は0.75から計算し始めるので、ダウンロード進捗度計算する前に引くように

    public AssetDownloader(string key)
    {
        this.key = key;
    }

    public string Key => key;

    public virtual void SetKey(string newKey)
    {
        if (key != newKey)
        {
            // 古い処理をキャンセル
            if (source != null && !source.Task.IsCompleted)
                source.TrySetCanceled();
        }
        key = newKey;
    }

    /// <summary>
    /// 関連のアセット全部ダウンロードする
    /// </summary>
    /// <param name="progress">進捗度出力</param>
    /// <param name="sizeProgress">ダウンロードしたサイズの進捗度出力</param>
    /// <param name="onFail">失敗時のリトライのコールバック</param>
    /// <returns></returns>
    public async UniTask<IAwaiter> DownloadDependenciesAsync(IProgress<(float ratio, long byteSize)> progress = null, Action<Action> onFail = null, Action<bool> subProgressUISwitch = null, IProgress<float> subProgress = null)
    {
        // キー設定ないの場合
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogWarning("アセットのキーが設定してないので、ダウンロードできません!");
            var canceledSource = new UniTaskCompletionSource();
            canceledSource.TrySetCanceled();
            return canceledSource;
        }
        // 既に処理中の場合
        if (source != null && !source.Task.IsCompleted)
        {
            // 完成するまで待つ
            await UniTask.WaitUntil(() => ((IAwaiter)source).IsCompleted);
            return source;
        }
        // ローカルSourceを生成
        var localSource = new UniTaskCompletionSource();
        source = localSource;
        // 失敗時のリトライのコールバックがない場合、リトライせずに終わる
        if (onFail == null)
        {
            onFail = (Action action) =>
            {
                localSource.TrySetCanceled();               //処理キャンセル
            };
        }
        // ダウンロード始まる
        await DownloadDependenciesAsync();
        // 完成するまで待つ
        await UniTask.WaitUntil(() => ((IAwaiter)localSource).IsCompleted);
        return localSource;

        // ローカル関数：再ダウンロード
        void ReDownload()
        {
            DownloadDependenciesAsync().Forget();
        }

        // ローカル関数：関連のアセット全部ダウンロードする
        async UniTask DownloadDependenciesAsync()
        {
            // アセットのサイズを取得
            var assetSize = await GetAssetDownloadSize();
            // 既にダウンロードした場合はスキップ
            if (assetSize == 0)
            {
                localSource.TrySetResult();                 //処理完成
                return;
            }
            // アセットのサイズを取得できない場合は中止
            else if (assetSize < 0)
            {
                onFail?.Invoke(ReDownload);
                return;
            }
            // 関連のアセットをダウンロード
            var downloadHandle = Addressables.DownloadDependenciesAsync(key, false);
            // ダウンロード待ち
            progress?.Report((0, assetSize));
            subProgress?.Report(0);
            subProgressUISwitch?.Invoke(true);
            while (!downloadHandle.IsDone)
            {
                await UniTask.Yield();                      //1フレーム待ち
                var progressRatio = (downloadHandle.PercentComplete - percentCompleteBase) / (1 - percentCompleteBase);
                progress?.Report((progressRatio, assetSize));
                subProgress?.Report(progressRatio);
                //Debug.Log("アセット: " + key + " のダウンロード進捗: " + ((downloadHandle.PercentComplete - percentCompleteBase) / (1 - percentCompleteBase) * 100).ToString("0.#") + "%");
            }
            progress?.Report((1, assetSize));
            subProgress?.Report(1);
            subProgressUISwitch?.Invoke(false);
            // ダウンロード終了処理
            if (downloadHandle.Status == AsyncOperationStatus.Succeeded)
            {
                Debug.Log("アセット: " + key + " はダウンロードしました、状態: " + downloadHandle.Status);
                localSource.TrySetResult();                 //処理完成
            }
            // エラー処理
            else
            {
                Debug.LogWarning("アセット: " + key + " のダウンロードはエラーが発生しました、状態: " + downloadHandle.Status + ", " + downloadHandle.OperationException);
                onFail?.Invoke(ReDownload);
            }
            Addressables.Release(downloadHandle);
        }
    }

    /// <summary>
    /// アセットのダウンロードサイズを取得
    /// </summary>
    public async UniTask<long> GetAssetDownloadSize()
    {
        // キー設定ないの場合
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogWarning("アセットのキーが設定してないので、ダウンロードサイズ確認できません!");
            return -1; ;
        }
        // ダウンロードサイズ取得
        long size = -1;
        var assetSizeHandle = Addressables.GetDownloadSizeAsync(key);
        await assetSizeHandle.Task;
        if (assetSizeHandle.Status == AsyncOperationStatus.Succeeded)
        {
            size = assetSizeHandle.Result;
        }
        else
        {
            Debug.LogWarning("アセット: " + key + " のダウンロードサイズが取得できません、状態: " + assetSizeHandle.Status + ", " + assetSizeHandle.OperationException);
        }
        Addressables.Release(assetSizeHandle);
        return size;
    }
}

[Serializable]
public abstract class AssetObjectBase<T> : AssetDownloader
{
    public T Value { get; protected set; } = default;
    protected AsyncOperationHandle<T> asyncOperationHandle;

    public AssetObjectBase(string key) : base(key) { }

    public bool IsValid()
    {
        return asyncOperationHandle.IsValid();
    }
}

[Serializable]
public class AssetObject<T> : AssetObjectBase<T>
{
    private IDisposable bindWithIDisposable;
    public AssetObject(string key) : base(key) { }

    public override void SetKey(string newKey)
    {
        if (key != newKey)
        {
            // 既にロード中orロードした場合
            if (asyncOperationHandle.IsValid())
            {
                ReleaseAsset();
            }
        }
        base.SetKey(newKey);
    }
    /// <summary>
    /// アセットをロード
    /// </summary>
    /// <param name="bindWith">バインドしたオブジェクトが消滅した場合こちらもアンロードする</param>
    /// <param name="progress">進捗度出力</param>
    /// <param name="observer">オブザーバー</param>
    /// <param name="onFail">失敗時のリトライのコールバック</param>
    public async UniTask LoadAssetAsync(GameObject bindWith = null, IProgress<float> progress = null, IObserver<T> observer = null, Action<Action> onFail = null, Action<bool> subProgressUISwitch = null, IProgress<float> subProgress = null)
    {
        // キー設定ないの場合
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogWarning("アセットのキーが設定してないので、ロードできません!");
            return;
        }
        // 既にロード中orロードした場合
        if (asyncOperationHandle.IsValid())
        {
            if (asyncOperationHandle.IsDone != true)
            {
                Debug.Log("アセット: " + key + " は既にロード中です");
                await asyncOperationHandle.Task;
            }
            else
            {
                Debug.Log("アセット: " + key + " は既にロードしました");
            }
            return;
        }
        // 先に関連のアセット全部ダウンロードする。既に処理中の場合はキャンセルしてくれる
        var awaiter = await DownloadDependenciesAsync(progress != null ? Progress.Create<(float ratio, long byteSize)>(p => progress.Report(p.ratio / 2)) : null, onFail, subProgressUISwitch, subProgress);
        // キャンセルした場合は何もしない
        if (awaiter.Status == AwaiterStatus.Canceled)
            return;
        // ローカルSourceを生成
        var localSource = new UniTaskCompletionSource();
        source = localSource;
        // 失敗時のリトライのコールバックがない場合、リトライせずに終わる
        if (onFail == null)
        {
            onFail = (Action action) =>
            {
                localSource.TrySetCanceled();               //処理キャンセル
            };
        }
        // ロード始まる
        await LoadAssetAsyncInternel();
        // 完成するまで待つ
        await UniTask.WaitUntil(() => ((IAwaiter)localSource).IsCompleted);
        return;

        // ローカル関数：再ロード
        void ReLoad()
        {
            LoadAssetAsyncInternel().Forget();
        }

        // ローカル関数：アセットをロード
        async UniTask LoadAssetAsyncInternel()
        {
            // キー設定ないの場合
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogWarning("アセットのキーが設定してないので、ロードできません!");
                localSource.TrySetCanceled();               //処理キャンセル
                return;
            }
            // 既にロード中orロードした場合
            if (asyncOperationHandle.IsValid())
            {
                if (asyncOperationHandle.IsDone != true)
                {
                    Debug.Log("アセット: " + key + " は既にロード中です");
                    await asyncOperationHandle.Task;
                }
                else
                {
                    Debug.Log("アセット: " + key + " は既にロードしました");
                }
                localSource.TrySetCanceled();               //処理キャンセル
                return;
            }
            // アセットをロード
            var handle = Addressables.LoadAssetAsync<T>(key);
            asyncOperationHandle = handle;
            if (progress != null)
            {
                progress.Report(0.5f);
                while (!handle.IsDone)
                {
                    await UniTask.Yield();                  //1フレーム待ち
                    progress.Report(0.5f + handle.PercentComplete / 2);
                }
                progress.Report(1);
            }
            else
            {
                await handle.Task;
            }
            // リザルト処理
            if (((IAwaiter)localSource).Status == AwaiterStatus.Canceled)
            {
                // キャンセルしたので、リリースする
                Addressables.Release(handle);               //アセットリリース
            }
            else if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                if (handle.Result == null)
                {
                    Exception exception = new Exception("アセット取得できましたが、内容がNullです!");
                    Debug.LogWarning("アセット: " + key + " はロードしました, 状態: " + handle.Status + ", " + exception);
                    // イベントとコールバックを送信
                    observer?.OnError(exception);
                    onFail?.Invoke(ReLoad);
                    Addressables.Release(handle);           //アセットリリース
                }
                else
                {
                    // リザルトを設定
                    Value = handle.Result;
                    Debug.Log("アセット: " + key + " はロードしました, 状態: " + handle.Status);
                    // アンロードのタイミングはオブジェクトと紐付ける
                    bindWithIDisposable = bindWith?.ObserveEveryValueChanged(_ => Unit.Default).Subscribe(_ => { }, () =>
                    {
                        if (handle.IsValid())
                        {
                            Value = default;
                            Addressables.Release(handle);   //アセットリリース
                        }
                    });
                    // イベントを送信
                    observer?.OnNext(handle.Result);
                    localSource.TrySetResult();             //処理完成
                }
            }
            else
            {   // エラー処理
                Debug.LogWarning("アセット: " + key + " のロードはエラーが発生しました、状態: " + handle.Status + ", " + handle.OperationException);
                // イベントとコールバックを送信
                observer?.OnError(handle.OperationException);
                onFail?.Invoke(ReLoad);
                Addressables.Release(handle);               //アセットリリース
            }
        }
    }

    public void ReleaseAsset()
    {
        if (asyncOperationHandle.IsValid())
        {
            Value = default;
            Addressables.Release(asyncOperationHandle);     //アセットリリース
            asyncOperationHandle = default;
            bindWithIDisposable?.Dispose();
        }
    }
}

[Serializable]
public class AssetScene : AssetObjectBase<SceneInstance>
{
    public AssetScene(string key) : base(key) { }

    public override void SetKey(string newKey)
    {
        if (key != newKey)
        {
            // 既にロード中orロードした場合
            if (asyncOperationHandle.IsValid())
            {
                UnloadSceneAsync().Forget();
            }
        }
        base.SetKey(newKey);
    }
    /// <summary>
    /// シーンをロード
    /// </summary>
    /// <param name="loadSceneMode">単一や追加読み込み</param>
    /// <param name="activateOnLoad">読み込みの後はアクティブしますか。しない場合は<see cref="SceneActivateAsync"/>でアクティブ化する必要があります</param>
    /// <param name="priority">非同期処理の優先度</param>
    /// <param name="progress">進捗度出力</param>
    /// <param name="observer">オブザーバー</param>
    /// <param name="onFail">失敗時のリトライのコールバック</param>
    public async UniTask LoadSceneAsync(LoadSceneMode loadSceneMode, bool activateOnLoad = true, int priority = 100, IProgress<float> progress = null, IObserver<SceneInstance> observer = null, Action<Action> onFail = null, Action<bool> subProgressUISwitch = null, IProgress<float> subProgress = null)
    {
        // キー設定ないの場合
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogWarning("アセットのキーが設定してないので、ロードできません!");
            return;
        }
        // 既にロード中orロードした場合
        if (asyncOperationHandle.IsValid())
        {
            if (asyncOperationHandle.IsDone != true)
            {
                Debug.Log("アセット: " + key + " のシーンは既にロード中です");
                await asyncOperationHandle.Task;
            }
            else
            {
                Debug.Log("アセット: " + key + " のシーンは既にロードしました");
            }
            return;
        }
        // 先に関連のアセット全部ダウンロードする。既に処理中の場合はキャンセルしてくれる
        var awaiter = await DownloadDependenciesAsync(progress != null ? Progress.Create<(float ratio, long byteSize)>(p => progress.Report(p.ratio / 2)) : null, onFail, subProgressUISwitch, subProgress);
        // キャンセルした場合は何もしない
        if (awaiter.Status == AwaiterStatus.Canceled)
            return;
        // ローカルSourceを生成
        var localSource = new UniTaskCompletionSource();
        source = localSource;
        // 失敗時のリトライのコールバックがない場合、リトライせずに終わる
        if (onFail == null)
        {
            onFail = (Action action) =>
            {
                localSource.TrySetCanceled();               //処理キャンセル
            };
        }
        // ロード始まる
        await LoadAssetAsyncInternel();
        // 完成するまで待つ
        await UniTask.WaitUntil(() => ((IAwaiter)localSource).IsCompleted);
        return;

        // ローカル関数：再ロード
        void ReLoad()
        {
            LoadAssetAsyncInternel().Forget();
        }

        // ローカル関数：シーンをロード
        async UniTask LoadAssetAsyncInternel()
        {
            // キー設定ないの場合
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogWarning("アセットのキーが設定してないので、ロードできません!");
                localSource.TrySetCanceled();               //処理キャンセル
                return;
            }
            // 既にロード中orロードした場合
            if (asyncOperationHandle.IsValid())
            {
                if (asyncOperationHandle.IsDone != true)
                {
                    Debug.Log("アセット: " + key + " のシーンは既にロード中です");
                    await asyncOperationHandle.Task;
                }
                else
                {
                    Debug.Log("アセット: " + key + " のシーンは既にロードしました");
                }
                localSource.TrySetCanceled();               //処理キャンセル
                return;
            }
            // シーンをロード
            var handle = Addressables.LoadSceneAsync(key, loadSceneMode, activateOnLoad, priority);
            asyncOperationHandle = handle;
            progress?.Report(0.5f);
            // ロード待ち
            while (!handle.IsDone)
            {
                await UniTask.Yield();                      //1フレーム待ち
                progress?.Report(0.5f + handle.PercentComplete / 2);
            }
            progress?.Report(1);
            // リザルト処理
            if (((IAwaiter)localSource).Status == AwaiterStatus.Canceled)
            {
                // キャンセルしたので、アンロードする
                await UnloadSceneAsync(handle, null, true);
            }
            else if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                // リザルトを設定
                Value = handle.Result;
                Debug.Log("アセット: " + key + " のシーンはロードしました、状態: " + handle.Status);
                // イベントを送信
                observer?.OnNext(handle.Result);
                localSource.TrySetResult();                 //処理完成
            }
            else
            {   // エラー処理
                Debug.LogWarning("アセット: " + key + " のシーンのロードはエラーが発生しました、状態: " + handle.Status + ", " + handle.OperationException);
                // イベントとコールバックを送信
                observer?.OnError(handle.OperationException);
                onFail?.Invoke(ReLoad);
            }
        }
    }

    /// <summary>
    /// ロードしたシーンをアクティブ化
    /// </summary>
    /// <param name="progress">進捗度出力</param>
    /// <returns></returns>
    public async UniTask SceneActivateAsync(IProgress<float> progress = null)
    {
        if (asyncOperationHandle.IsValid())
        {
            await Value.ActivateAsync().ConfigureAwait(Progress.Create<float>(p => progress?.Report(p)));
        }
    }

    /// <summary>
    /// シーンをアンロード
    /// </summary>
    /// <param name="progress">進捗度出力</param>
    /// <returns></returns>
    public async UniTask UnloadSceneAsync(IProgress<float> progress = null)
    {
        await UnloadSceneAsync(asyncOperationHandle, progress);
    }

    protected async UniTask UnloadSceneAsync(AsyncOperationHandle<SceneInstance> handle, IProgress<float> progress = null, bool isCancel = false)
    {
        if (handle.IsValid())
        {
            var unloadHandle = Addressables.UnloadSceneAsync(handle);
            while (!unloadHandle.IsDone)
            {
                progress?.Report(unloadHandle.PercentComplete);       //完成したから呼ぶとエラー発生するので、待ち行動の前にやる
                await UniTask.Yield();                  //1フレーム待ち
            }
            if (isCancel == false)
                Debug.Log("アセット: " + key + " はアンロードしました");
            else
                Debug.Log("アセット: " + key + " のロードはキャンセルしました");
        }
    }
}

// ジェネリック隠し
[Serializable]
public class AssetObjectGameObject : AssetObject<GameObject>
{
    public AssetObjectGameObject(string key) : base(key) { }
}